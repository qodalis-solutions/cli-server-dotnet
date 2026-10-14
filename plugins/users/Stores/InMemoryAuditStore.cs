using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Stores;

/// <summary>
/// In-memory implementation of <see cref="IAuditStore"/>. Keeps the most recent
/// <see cref="MaxEntries"/> entries (default 1000).
/// </summary>
public class InMemoryAuditStore : IAuditStore
{
    /// <summary>Default number of retained entries.</summary>
    public const int DefaultMaxEntries = 1000;

    private readonly LinkedList<AuditEntry> _entries = new();
    private readonly object _lock = new();

    public InMemoryAuditStore() : this(DefaultMaxEntries) { }

    public InMemoryAuditStore(int maxEntries)
    {
        MaxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
    }

    /// <summary>Maximum number of retained entries; the oldest are dropped first.</summary>
    public int MaxEntries { get; }

    public Task LogAsync(AuditEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Id))
            entry.Id = Guid.NewGuid().ToString();

        lock (_lock)
        {
            _entries.AddLast(entry);
            while (_entries.Count > MaxEntries)
                _entries.RemoveFirst();
        }
        return Task.CompletedTask;
    }

    public Task<List<AuditEntry>> GetEntriesAsync(AuditQueryOptions? options = null)
    {
        lock (_lock)
        {
            // Newest first; insertion order breaks timestamp ties.
            IEnumerable<AuditEntry> query = _entries.Reverse()
                .OrderByDescending(e => e.Timestamp);

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
        }
        return Task.CompletedTask;
    }
}
