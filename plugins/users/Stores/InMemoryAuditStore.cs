using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Stores;

/// <summary>
/// In-memory implementation of <see cref="IAuditStore"/>.
/// </summary>
public class InMemoryAuditStore : IAuditStore
{
    private readonly List<AuditEntry> _entries = [];
    private readonly object _lock = new();

    public Task LogAsync(AuditEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
        }
        return Task.CompletedTask;
    }

    public Task<List<AuditEntry>> GetEntriesAsync(AuditQueryOptions? options = null)
    {
        lock (_lock)
        {
            IEnumerable<AuditEntry> query = _entries.OrderByDescending(e => e.Timestamp);

            if (options?.Event != null)
                query = query.Where(e => e.Event == options.Event);

            if (options?.Username != null)
                query = query.Where(e => e.Username == options.Username);

            if (options?.Limit.HasValue == true)
                query = query.Take(options.Limit.Value);

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
