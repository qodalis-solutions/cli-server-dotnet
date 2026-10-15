using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Qodalis.Cli.Services;

/// <summary>
/// Ends a shell together with everything it started (Unix). The process list is read once with
/// <c>ps -A -o pid=,ppid=,pgid=,tty=</c> (macOS and Linux) BEFORE any signal is sent, so children
/// cannot escape by being re-parented while the tree is being killed. Targets:
/// <list type="bullet">
/// <item>every descendant of the root process (by parent PID), including <c>&amp;</c> jobs in their own
/// process groups and <c>setsid</c>/<c>nohup</c> children that are still children of the shell;</item>
/// <item>every process still attached to the shell's terminal (e.g. <c>( cmd &amp; )</c> whose subshell
/// already exited);</item>
/// <item>the process groups of those processes (never the server's own group).</item>
/// </list>
/// They receive <c>SIGHUP</c>, then <c>SIGTERM</c>, and after a grace period <c>SIGKILL</c>.
/// Limitation: a daemon that double-forks AND leaves the terminal (<c>setsid</c>) after its parent
/// exited is re-parented to init and can no longer be attributed to the shell.
/// On Windows (no <c>ps</c>) the process tree is killed with <see cref="Process.Kill(bool)"/>.
/// </summary>
internal static class ShellProcessTree
{
    /// <summary>Time between <c>SIGTERM</c> and <c>SIGKILL</c>.</summary>
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(2);

    private sealed record ProcessEntry(int Pid, int Ppid, int Pgid, string? Tty);

    /// <summary>The processes and process groups to signal for the tree rooted at <paramref name="rootPid"/>.</summary>
    internal sealed record Targets(IReadOnlyList<int> Pids, IReadOnlyList<int> Groups);

    /// <summary>
    /// Signals the whole tree of <paramref name="process"/> (HUP, TERM, then KILL after
    /// <see cref="GracePeriod"/>). The KILL step runs in the background; this call returns
    /// once the tree is collected and the first signals are sent.
    /// </summary>
    internal static void Terminate(Process process, ILogger? logger = null)
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

        var targets = Collect(rootPid);
        if (targets == null)
        {
            KillTreeFallback(process, logger);
            return;
        }

        Signal("HUP", targets, logger);
        Signal("TERM", targets, logger);
        _ = Task.Run(async () =>
        {
            await Task.Delay(GracePeriod);
            // Only processes that still exist with the same parent/group are killed (PID reuse).
            var remaining = Snapshot();
            if (remaining == null)
            {
                Signal("KILL", targets, logger);
                return;
            }
            var alive = remaining.Select(p => p.Pid).ToHashSet();
            var groups = remaining.Select(p => p.Pgid).ToHashSet();
            var survivors = new Targets(
                targets.Pids.Where(alive.Contains).ToList(),
                targets.Groups.Where(groups.Contains).ToList());
            if (survivors.Pids.Count > 0 || survivors.Groups.Count > 0)
                Signal("KILL", survivors, logger);
        });
    }

    /// <summary>
    /// Collects the tree of <paramref name="rootPid"/> (see the class summary). Null when the
    /// process list cannot be read.
    /// </summary>
    internal static Targets? Collect(int rootPid)
    {
        var all = Snapshot();
        if (all == null) return null;

        var self = Environment.ProcessId;
        var ownGroup = all.FirstOrDefault(p => p.Pid == self)?.Pgid;

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

        var groups = all
            .Where(p => pids.Contains(p.Pid) && p.Pgid > 1 && p.Pgid != ownGroup)
            .Select(p => p.Pgid)
            .Distinct()
            .ToList();
        pids.Remove(self);
        return new Targets(pids.ToList(), groups);
    }

    private static List<ProcessEntry>? Snapshot()
    {
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

    private static void Signal(string signal, Targets targets, ILogger? logger)
    {
        if (targets.Pids.Count == 0 && targets.Groups.Count == 0) return;
        // A negative PID after "--" addresses a process group; "kill" keeps going when some
        // targets are already gone (non-zero exit, ignored).
        var args = new List<string> { "-s", signal, "--" };
        args.AddRange(targets.Groups.Select(g => "-" + g));
        args.AddRange(targets.Pids.Select(p => p.ToString()));
        if (Run("kill", args) == null)
            logger?.LogDebug("Could not send SIG{Signal} to the shell process tree", signal);
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
