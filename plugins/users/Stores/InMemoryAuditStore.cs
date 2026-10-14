using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Stores;

/// <summary>
/// In-memory implementation of <see cref="IAuditStore"/>. Keeps the most recent
/// <see cref="MaxEntries"/> entries (default 1000); <c>login_failed</c> entries are capped
/// separately (<see cref="MaxFailedLoginEntries"/>, default 200) so they cannot evict other history.
/// </summary>
public class InMemoryAuditStore : IAuditStore
{
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

    public Task LogAsync(AuditEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Id))
            entry.Id = Guid.NewGuid().ToString();

        lock (_lock)
        {
            var failed = entry.Event == AuditEvents.LoginFailed;
            var list = failed ? _failedLogins : _entries;
            var cap = failed ? MaxFailedLoginEntries : MaxEntries;
            list.AddLast((++_seq, entry));
            while (list.Count > cap)
                list.RemoveFirst();
        }
        return Task.CompletedTask;
    }

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

            if (options?.Limit is int limit)
                query = query.Take(limit);

            return Task.FromResult(query.ToList());
        }
    }

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
