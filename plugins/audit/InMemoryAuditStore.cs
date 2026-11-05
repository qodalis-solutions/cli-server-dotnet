using Qodalis.Cli.Abstractions.Audit;

namespace Qodalis.Cli.Plugin.Audit;

/// <summary>
/// In-memory implementation of <see cref="IAuditStore"/>. Keeps the most recent
/// <see cref="MaxEntries"/> entries (default 1000); <c>login_failed</c> entries are capped
/// separately (<see cref="MaxFailedLoginEntries"/>, default 200) so they cannot evict other history.
/// </summary>
public class InMemoryAuditStore : IAuditStore
{
    /// <summary>The event of a failed sign-in (users plugin), kept apart so a flood of them keeps the rest.</summary>
    public const string FailedLoginEvent = "login_failed";

    public const int DefaultMaxEntries = 1000;
    public const int DefaultMaxFailedLoginEntries = 200;

    private readonly LinkedList<(long Seq, AuditEntry Entry)> _entries = new();
    private readonly LinkedList<(long Seq, AuditEntry Entry)> _failedLogins = new();
    private readonly object _lock = new();
    private long _seq;

    public InMemoryAuditStore() : this(DefaultMaxEntries) { }

    public InMemoryAuditStore(int maxEntries, int maxFailedLoginEntries = DefaultMaxFailedLoginEntries)
    {
        MaxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
        MaxFailedLoginEntries = maxFailedLoginEntries > 0 ? maxFailedLoginEntries : DefaultMaxFailedLoginEntries;
    }

    /// <summary>Maximum number of retained entries other than <c>login_failed</c>.</summary>
    public int MaxEntries { get; }

    /// <summary>Maximum number of retained <c>login_failed</c> entries.</summary>
    public int MaxFailedLoginEntries { get; }

    /// <inheritdoc />
    public Task LogAsync(AuditEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Id))
            entry.Id = Guid.NewGuid().ToString();

        lock (_lock)
        {
            var failed = entry.Event == FailedLoginEvent;
            var list = failed ? _failedLogins : _entries;
            var cap = failed ? MaxFailedLoginEntries : MaxEntries;
            list.AddLast((++_seq, entry));
            while (list.Count > cap)
                list.RemoveFirst();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<List<AuditEntry>> GetEntriesAsync(AuditQueryOptions? options = null)
    {
        lock (_lock)
        {
            // Newest first; insertion order breaks timestamp ties.
            IEnumerable<AuditEntry> query = _entries.Concat(_failedLogins)
                .OrderByDescending(e => e.Entry.Timestamp)
                .ThenByDescending(e => e.Seq)
                .Select(e => e.Entry);

            if (!string.IsNullOrEmpty(options?.Event))
                query = query.Where(e => string.Equals(e.Event, options.Event, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(options?.Username))
                query = query.Where(e => string.Equals(e.Username, options.Username, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(options?.Actor))
                query = query.Where(e => string.Equals(e.Actor, options.Actor, StringComparison.OrdinalIgnoreCase));

            if (options?.Offset is int offset and > 0)
                query = query.Skip(offset);

            if (options?.Limit is int limit)
                query = query.Take(limit);

            return Task.FromResult(query.ToList());
        }
    }

    /// <inheritdoc />
    public Task<int> PruneAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        var cutoff = before.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            var removed = 0;
            foreach (var list in new[] { _entries, _failedLogins })
            {
                for (var node = list.First; node != null;)
                {
                    var next = node.Next;
                    if (node.Value.Entry.Timestamp < cutoff)
                    {
                        list.Remove(node);
                        removed++;
                    }
                    node = next;
                }
            }
            return Task.FromResult(removed);
        }
    }

    /// <inheritdoc />
    public Task ClearAsync()
    {
        lock (_lock)
        {
            _entries.Clear();
            _failedLogins.Clear();
        }
        return Task.CompletedTask;
    }
}
