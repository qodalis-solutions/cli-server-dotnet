namespace Qodalis.Cli.Plugin.PublicApi;

/// <summary>
/// Request counters per token and window: a UTC day (<c>yyyy-MM-dd</c>) or a UTC minute (<c>yyyy-MM-ddTHH:mm</c>).
/// A persistent store shared by several processes (<c>Qodalis.Cli.Plugin.PublicApi.EfCore</c>) makes the limits hold
/// across them. The guard admits a request by incrementing the token's day and minute counters, reading the account's
/// sums and, when a limit is exceeded, decrementing both again (fails closed under contention).
/// </summary>
public interface IPublicApiUsageStore
{
    /// <summary>Adds one request to <paramref name="tokenId"/>'s counter for <paramref name="window"/>; returns the new count.</summary>
    Task<long> IncrementAsync(string tokenId, string userId, string window, CancellationToken ct);

    /// <summary>Takes back one request (a refused admission).</summary>
    Task DecrementAsync(string tokenId, string window, CancellationToken ct);

    /// <summary>The account's requests in <paramref name="window"/>: the sum over all its tokens, revoked ones included.</summary>
    Task<long> GetAccountCountAsync(string userId, string window, CancellationToken ct);

    /// <summary>Day counters of <paramref name="tokenIds"/> from <paramref name="fromDay"/> to <paramref name="toDay"/> (inclusive): token id → day → count; days without requests may be missing.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>>> GetTokenDaysAsync(
        IReadOnlyCollection<string> tokenIds, string fromDay, string toDay, CancellationToken ct);

    /// <summary>Removes day counters before <paramref name="beforeDay"/> and minute counters before <paramref name="beforeMinute"/>.</summary>
    Task PruneAsync(string beforeDay, string beforeMinute, CancellationToken ct);
}

/// <summary>Window keys and their arithmetic, all in UTC.</summary>
public static class PublicApiWindows
{
    /// <summary>Length of a day window (<c>yyyy-MM-dd</c>).</summary>
    public const int DayLength = 10;

    /// <summary>Length of a minute window (<c>yyyy-MM-ddTHH:mm</c>).</summary>
    public const int MinuteLength = 16;

    /// <summary>The UTC day of <paramref name="now"/>.</summary>
    public static string Day(DateTimeOffset now) =>
        now.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The UTC minute of <paramref name="now"/>.</summary>
    public static string Minute(DateTimeOffset now) =>
        now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>True for a day window, false for a minute window.</summary>
    public static bool IsDay(string window) => window.Length == DayLength;
}
