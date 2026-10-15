using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Qodalis.Cli.Services;

/// <summary>
/// Ends a shell together with everything it started (Unix). The process list is read once
/// (Linux: <c>/proc</c>; macOS, or Linux without <c>/proc</c>: <c>ps -A -o pid=,ppid=,pgid=,tty=</c>)
/// BEFORE any signal is sent, so children cannot escape by being re-parented while the tree is
/// being killed. Targets:
/// <list type="bullet">
/// <item>every descendant of the root process (by parent PID), including <c>&amp;</c> jobs in their own
/// process groups and <c>setsid</c>/<c>nohup</c> children that are still children of the shell;</item>
/// <item>every process still attached to the shell's terminal (e.g. <c>( cmd &amp; )</c> whose subshell
/// already exited);</item>
/// <item>every process whose environment carries the shell's marker
/// <c><see cref="MarkerVariable"/>=&lt;id&gt;</c> (set for each shell, inherited by everything it starts),
/// which catches daemons that double-forked and left the terminal (<c>setsid cmd &amp;</c>);</item>
/// <item>the process groups of those processes (never the server's own group).</item>
/// </list>
/// They receive <c>SIGHUP</c>, then <c>SIGTERM</c>, and after a grace period <c>SIGKILL</c> (the marked
/// processes are collected again right before it). Only a process that deliberately unsets the
/// marker (or runs as another user) escapes; on macOS <c>ps -E</c> does not show the environment
/// of Apple platform binaries, so a detached system binary is not found by its marker there.
/// On Windows the process tree is killed with <see cref="Process.Kill(bool)"/>.
/// </summary>
internal static class ShellProcessTree
{
    /// <summary>Environment variable that tags every process started by one shell.</summary>
    internal const string MarkerVariable = "QCLI_SHELL_ID";

    /// <summary>Time between <c>SIGTERM</c> and <c>SIGKILL</c>.</summary>
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(2);

    private const int SIGHUP = 1;
    private const int SIGKILL = 9;
    private const int SIGTERM = 15;

    internal sealed record ProcessEntry(int Pid, int Ppid, int Pgid, string? Tty);

    /// <summary>The processes and process groups to signal for the tree rooted at a shell.</summary>
    internal sealed record Targets(IReadOnlyList<int> Pids, IReadOnlyList<int> Groups);

    /// <summary>A new random 128-bit marker id (32 lowercase hex characters).</summary>
    internal static string NewMarker() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// Signals the whole tree of <paramref name="process"/> plus every process carrying
    /// <paramref name="marker"/> (HUP, TERM, then KILL after <see cref="GracePeriod"/>). The KILL
    /// step runs in the background; this call returns once the targets are collected and the
    /// first signals are sent.
    /// </summary>
    internal static void Terminate(Process process, string? marker, ILogger? logger = null)
    {
        int rootPid;
        try
        {
            if (process.HasExited) return;
            rootPid = process.Id;
        }
        catch (InvalidOperationException)
        {
            return; // never started
        }

        if (OperatingSystem.IsWindows())
        {
            KillTreeFallback(process, logger);
            return;
        }

        var targets = Collect(rootPid, marker);
        if (targets == null)
        {
            KillTreeFallback(process, logger);
            return;
        }

        Signal(SIGHUP, targets, logger);
        Signal(SIGTERM, targets, logger);
        _ = Task.Run(async () =>
        {
            await Task.Delay(GracePeriod);
            // Only processes and groups that still exist are killed (PID reuse); processes that
            // picked up the marker since (started during the grace period) are added.
            var remaining = Snapshot();
            if (remaining == null)
            {
                Signal(SIGKILL, targets, logger);
                return;
            }
            var alive = remaining.Select(p => p.Pid).ToHashSet();
            var groups = remaining.Select(p => p.Pgid).ToHashSet();
            var pids = targets.Pids.Where(alive.Contains).ToHashSet();
            var groupSet = targets.Groups.Where(groups.Contains).ToHashSet();
            var marked = AddMarked(remaining, marker, pids);
            foreach (var g in GroupsOf(remaining, marked)) groupSet.Add(g);
            Signal(SIGKILL, new Targets(pids.ToList(), groupSet.ToList()), logger);
        });
    }

    /// <summary>
    /// Collects the tree of <paramref name="rootPid"/> and the processes carrying
    /// <paramref name="marker"/> (see the class summary). Null when the process list cannot be read.
    /// </summary>
    internal static Targets? Collect(int rootPid, string? marker = null)
    {
        var all = Snapshot();
        if (all == null) return null;

        var self = Environment.ProcessId;

        var byParent = all.ToLookup(p => p.Ppid);
        var pids = new HashSet<int> { rootPid };
        var queue = new Queue<int>([rootPid]);
        while (queue.Count > 0)
        {
            foreach (var child in byParent[queue.Dequeue()])
            {
                if (child.Pid != self && pids.Add(child.Pid)) queue.Enqueue(child.Pid);
            }
        }

        // Processes still on one of the tree's terminals (the PTY that script allocated). The
        // server's own terminal (inherited by the root when the server runs in one) is never used.
        var foreignTtys = all.Where(p => (p.Pid == self || p.Pid == rootPid) && p.Tty != null)
            .Select(p => p.Tty!).ToHashSet();
        var ttys = all.Where(p => pids.Contains(p.Pid) && p.Tty != null && !foreignTtys.Contains(p.Tty!))
            .Select(p => p.Tty!).ToHashSet();
        foreach (var p in all.Where(p => p.Tty != null && ttys.Contains(p.Tty!) && p.Pid != self))
            pids.Add(p.Pid);

        AddMarked(all, marker, pids);

        pids.Remove(self);
        return new Targets(pids.ToList(), GroupsOf(all, pids));
    }

    /// <summary>Adds the processes carrying <paramref name="marker"/> to <paramref name="pids"/>; returns them.</summary>
    private static HashSet<int> AddMarked(List<ProcessEntry> all, string? marker, HashSet<int> pids)
    {
        var marked = FindMarked(marker);
        marked.Remove(Environment.ProcessId);
        marked.IntersectWith(all.Select(p => p.Pid));
        pids.UnionWith(marked);
        return marked;
    }

    /// <summary>The process groups of <paramref name="pids"/>, never init's or the server's own group.</summary>
    private static List<int> GroupsOf(List<ProcessEntry> all, IReadOnlySet<int> pids)
    {
        var self = Environment.ProcessId;
        var ownGroup = all.FirstOrDefault(p => p.Pid == self)?.Pgid;
        return all
            .Where(p => pids.Contains(p.Pid) && p.Pid != self && p.Pgid > 1 && p.Pgid != ownGroup)
            .Select(p => p.Pgid)
            .Distinct()
            .ToList();
    }

    // ------------------------------------------------------------------ marker lookup

    /// <summary>The marker entry exactly as it appears in a process environment.</summary>
    internal static string MarkerEntry(string marker) => $"{MarkerVariable}={marker}";

    /// <summary>True when a NUL-separated environment block (<c>/proc/&lt;pid&gt;/environ</c>) contains exactly the marker entry.</summary>
    internal static bool EnvironContainsMarker(ReadOnlySpan<byte> environ, string marker)
    {
        if (!IsValidMarker(marker)) return false;
        var entry = Encoding.ASCII.GetBytes(MarkerEntry(marker));
        while (environ.Length > 0)
        {
            var end = environ.IndexOf((byte)0);
            var item = end < 0 ? environ : environ[..end];
            if (item.SequenceEqual(entry)) return true;
            if (end < 0) break;
            environ = environ[(end + 1)..];
        }
        return false;
    }

    /// <summary>
    /// The PIDs in <c>ps -A -E -ww -o pid=,command=</c> output (macOS) whose line contains the
    /// marker entry as a whole whitespace-separated token after the PID.
    /// </summary>
    internal static HashSet<int> ParsePsEnvironment(string output, string marker)
    {
        var result = new HashSet<int>();
        if (!IsValidMarker(marker)) return result;
        var entry = MarkerEntry(marker);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || !int.TryParse(tokens[0], out var pid)) continue;
            if (tokens.Skip(1).Any(t => t == entry)) result.Add(pid);
        }
        return result;
    }

    private static bool IsValidMarker(string? marker) =>
        !string.IsNullOrEmpty(marker) && marker.All(Uri.IsHexDigit);

    private static HashSet<int> FindMarked(string? marker)
    {
        if (!IsValidMarker(marker)) return [];
        if (OperatingSystem.IsLinux() && Directory.Exists("/proc/self"))
        {
            var result = new HashSet<int>();
            foreach (var pid in ProcPids())
            {
                try
                {
                    if (EnvironContainsMarker(File.ReadAllBytes($"/proc/{pid}/environ"), marker!))
                        result.Add(pid);
                }
                catch (Exception)
                {
                    // gone or not readable (another user)
                }
            }
            return result;
        }
        var output = Run("ps", ["-A", "-E", "-ww", "-o", "pid=,command="]);
        return output == null ? [] : ParsePsEnvironment(output, marker!);
    }

    // ------------------------------------------------------------------ process list

    private static List<ProcessEntry>? Snapshot()
    {
        if (OperatingSystem.IsLinux() && Directory.Exists("/proc/self"))
        {
            var fromProc = ProcSnapshot();
            if (fromProc.Count > 0) return fromProc;
        }
        var output = Run("ps", ["-A", "-o", "pid=,ppid=,pgid=,tty="]);
        if (output == null) return null;

        var list = new List<ProcessEntry>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 ||
                !int.TryParse(parts[0], out var pid) ||
                !int.TryParse(parts[1], out var ppid) ||
                !int.TryParse(parts[2], out var pgid))
                continue;
            // "?" (Linux) and "??" (macOS) mean no controlling terminal.
            var tty = parts.Length > 3 && parts[3].Trim('?', '-').Length > 0 ? parts[3] : null;
            list.Add(new ProcessEntry(pid, ppid, pgid, tty));
        }
        return list.Count > 0 ? list : null;
    }

    private static IEnumerable<int> ProcPids()
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories("/proc").ToList(); }
        catch (Exception) { yield break; }
        foreach (var dir in dirs)
        {
            if (int.TryParse(Path.GetFileName(dir), out var pid)) yield return pid;
        }
    }

    private static List<ProcessEntry> ProcSnapshot()
    {
        var list = new List<ProcessEntry>();
        foreach (var pid in ProcPids())
        {
            string stat;
            try { stat = File.ReadAllText($"/proc/{pid}/stat"); }
            catch (Exception) { continue; } // exited meanwhile
            var entry = ParseProcStat(stat);
            if (entry != null) list.Add(entry);
        }
        return list;
    }

    /// <summary>
    /// Parses <c>/proc/&lt;pid&gt;/stat</c>: <c>pid (comm) state ppid pgrp session tty_nr ...</c>
    /// (comm may contain spaces and parentheses, so fields are read after the last <c>)</c>).
    /// A zero <c>tty_nr</c> means no controlling terminal. Zombies are skipped.
    /// </summary>
    internal static ProcessEntry? ParseProcStat(string stat)
    {
        var open = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');
        if (open <= 0 || close < open) return null;
        if (!int.TryParse(stat.AsSpan(0, open).Trim(), out var pid)) return null;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5 ||
            fields[0] == "Z" ||
            !int.TryParse(fields[1], out var ppid) ||
            !int.TryParse(fields[2], out var pgid) ||
            !int.TryParse(fields[4], out var ttyNr))
            return null;
        return new ProcessEntry(pid, ppid, pgid, ttyNr != 0 ? ttyNr.ToString() : null);
    }

    // ------------------------------------------------------------------ signals

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int sig);

    private static void Signal(int signal, Targets targets, ILogger? logger)
    {
        // A negative PID addresses a process group; targets that are already gone are ignored.
        var failed = 0;
        foreach (var target in targets.Groups.Select(g => -g).Concat(targets.Pids))
        {
            if (target == 0 || target == -1 || target == 1) continue;
            try
            {
                if (SysKill(target, signal) != 0) failed++;
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Could not send signal {Signal} to the shell process tree", signal);
                return;
            }
        }
        if (failed > 0)
            logger?.LogTrace("Signal {Signal}: {Failed} target(s) already gone", signal, failed);
    }

    private static void KillTreeFallback(Process process, ILogger? logger)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed to kill shell process tree");
        }
    }

    /// <summary>Runs a short helper command; its stdout, or null when it could not run.</summary>
    private static string? Run(string fileName, IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var helper = Process.Start(psi);
            if (helper == null) return null;
            var stderr = helper.StandardError.ReadToEndAsync();
            var output = helper.StandardOutput.ReadToEnd();
            if (!helper.WaitForExit(5000))
            {
                try { helper.Kill(); } catch (Exception) { }
                return null;
            }
            _ = stderr.Result;
            return output;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
