namespace Qodalis.Cli.Plugin.PublicApi;

/// <summary>
/// The default usage store: counters in memory, one process only, lost on restart. Use the EF Core store when several
/// processes serve the API or usage must survive restarts.
/// </summary>
public sealed class InMemoryPublicApiUsageStore : IPublicApiUsageStore
{
    private readonly object _lock = new();
    private readonly Dictionary<(string TokenId, string Window), (string UserId, long Count)> _counters = new();

    public Task<long> IncrementAsync(string tokenId, string userId, string window, CancellationToken ct)
    {
        lock (_lock)
        {
            var count = _counters.TryGetValue((tokenId, window), out var row) ? row.Count + 1 : 1;
            _counters[(tokenId, window)] = (userId, count);
            return Task.FromResult(count);
        }
    }

    public Task DecrementAsync(string tokenId, string window, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_counters.TryGetValue((tokenId, window), out var row))
            {
                if (row.Count <= 1) _counters.Remove((tokenId, window));
                else _counters[(tokenId, window)] = (row.UserId, row.Count - 1);
            }
        }
        return Task.CompletedTask;
    }

    public Task<long> GetAccountCountAsync(string userId, string window, CancellationToken ct)
    {
        lock (_lock)
        {
            long sum = 0;
            foreach (var (key, row) in _counters)
            {
                if (key.Window == window && row.UserId == userId) sum += row.Count;
            }
            return Task.FromResult(sum);
        }
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>>> GetTokenDaysAsync(
        IReadOnlyCollection<string> tokenIds, string fromDay, string toDay, CancellationToken ct)
    {
        var ids = tokenIds.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, Dictionary<string, long>>();
        lock (_lock)
        {
            foreach (var (key, row) in _counters)
            {
                if (!ids.Contains(key.TokenId) || !PublicApiWindows.IsDay(key.Window)) continue;
                if (string.CompareOrdinal(key.Window, fromDay) < 0 || string.CompareOrdinal(key.Window, toDay) > 0) continue;
                if (!result.TryGetValue(key.TokenId, out var days))
                    result[key.TokenId] = days = new Dictionary<string, long>();
                days[key.Window] = row.Count;
            }
        }
        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>>>(
            result.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, long>)p.Value));
    }

    public Task PruneAsync(string beforeDay, string beforeMinute, CancellationToken ct)
    {
        lock (_lock)
        {
            foreach (var key in _counters.Keys.ToList())
            {
                var before = PublicApiWindows.IsDay(key.Window) ? beforeDay : beforeMinute;
                if (string.CompareOrdinal(key.Window, before) < 0) _counters.Remove(key);
            }
        }
        return Task.CompletedTask;
    }
}
