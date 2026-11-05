using System.Collections.Concurrent;

namespace Qodalis.Cli.Services;

/// <summary>
/// Counts command executions since the server started (<c>GET /api/v1/qcli/commands/stats</c>): per root command, the
/// executions, failures (exit code other than 0, or an exception), total duration and the last times. In memory and
/// thread-safe; commands the registry does not know are counted under their name too.
/// </summary>
public sealed class CliCommandUsage
{
    private readonly ConcurrentDictionary<string, Counter> _commands = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>Starts counting now.</summary>
    public CliCommandUsage() : this(TimeProvider.System) { }

    /// <summary>Starts counting now, by <paramref name="time"/>'s clock.</summary>
    public CliCommandUsage(TimeProvider time)
    {
        _time = time;
        Since = time.GetUtcNow().UtcDateTime;
    }

    /// <summary>When counting started (UTC).</summary>
    public DateTime Since { get; }

    /// <summary>The key of a command line: its first word, lower-case.</summary>
    public static string Key(string? command)
    {
        var trimmed = (command ?? string.Empty).Trim();
        var space = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        return (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
    }

    /// <summary>Counts one execution of <paramref name="command"/> (its first word).</summary>
    /// <param name="command">The command, or a command line.</param>
    /// <param name="known">Whether the registry resolved it.</param>
    /// <param name="duration">How long it ran.</param>
    /// <param name="failed">Whether it exited with a code other than 0 or threw.</param>
    public void Record(string? command, bool known, TimeSpan duration, bool failed)
    {
        var key = Key(command);
        if (key.Length == 0) return;

        var now = _time.GetUtcNow().UtcDateTime;
        var counter = _commands.GetOrAdd(key, _ => new Counter());
        lock (counter)
        {
            counter.Known |= known;
            counter.Executions++;
            counter.TotalMs += Math.Max(0, duration.TotalMilliseconds);
            counter.LastExecutedAt = now;
            if (failed)
            {
                counter.Failures++;
                counter.LastFailedAt = now;
            }
        }
    }

    /// <summary>Every counted command, by executions (most first), then by name.</summary>
    public IReadOnlyList<CliCommandUsageEntry> GetEntries() =>
        _commands.Select(pair =>
            {
                lock (pair.Value)
                {
                    var c = pair.Value;
                    return new CliCommandUsageEntry(
                        pair.Key, c.Known, c.Executions, c.Failures,
                        c.Executions == 0 ? 0 : Math.Round(c.TotalMs / c.Executions, 2),
                        c.LastExecutedAt, c.LastFailedAt);
                }
            })
            .OrderByDescending(e => e.Executions)
            .ThenBy(e => e.Command, StringComparer.Ordinal)
            .ToList();

    private sealed class Counter
    {
        public bool Known;
        public long Executions;
        public long Failures;
        public double TotalMs;
        public DateTime? LastExecutedAt;
        public DateTime? LastFailedAt;
    }
}

/// <summary>The counts of one command in <see cref="CliCommandUsage"/>.</summary>
/// <param name="Command">The root command name, lower-case.</param>
/// <param name="Known">Whether the registry knew it.</param>
/// <param name="Executions">How many times it ran.</param>
/// <param name="Failures">How many runs failed.</param>
/// <param name="AvgDuration">The mean duration in milliseconds.</param>
/// <param name="LastExecutedAt">When it last ran (UTC).</param>
/// <param name="LastFailedAt">When it last failed (UTC), or null.</param>
public sealed record CliCommandUsageEntry(
    string Command, bool Known, long Executions, long Failures, double AvgDuration, DateTime? LastExecutedAt, DateTime? LastFailedAt);
