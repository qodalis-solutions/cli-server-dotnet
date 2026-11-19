using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Plugin.Users.Authorization;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Services;

namespace Qodalis.Cli.Plugin.PublicApi;

/// <summary>
/// The check behind <c>.RequirePublicApi(...)</c> and <c>[RequirePublicApi]</c>, in order (the first failure answers and
/// nothing is counted): authentication (the users guard's texts), a <c>host</c> token, the permission, the quota
/// resolver (500 when it throws), a zero limit (403), the daily then the per-minute limit (429). An admitted request is
/// counted on its token's day and minute counters before the handler runs. The <c>X-RateLimit-*</c> headers describe the
/// daily limit when there is one, otherwise the per-minute limit.
/// </summary>
internal sealed class PublicApiGuard
{
    internal const string NoAccessMessage = "forbidden: no API access";
    internal const string InternalErrorMessage = "internal server error";
    private const int AdmissionStripes = 64;

    private readonly PublicApiOptions _options;
    private readonly IPublicApiUsageStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<PublicApiGuard> _logger;
    // Admissions of one account are serialized in this process, so an in-process store admits exactly the limit;
    // across processes the store's increment-then-check-then-compensate fails closed.
    private readonly SemaphoreSlim[] _admission = Enumerable.Range(0, AdmissionStripes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private long _lastPruneTicks = long.MinValue;

    public PublicApiGuard(PublicApiOptions options, IPublicApiUsageStore store, TimeProvider time, ILogger<PublicApiGuard> logger)
    {
        _options = options;
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <summary>Null when the request is admitted (headers set, request counted); otherwise the refusal.</summary>
    public async Task<(int Status, string Error)?> CheckAsync(HttpContext http, string? permission)
    {
        // Rows 1-2: authentication and the host-token check (the users guard's texts).
        if (await QcliAuthorization.AuthorizeAsync(http, null, hostTokenOnly: true) is { } failure) return failure;
        var auth = http.GetUsersAuth()!;
        var caller = http.GetQcliCaller()!;

        // Row 3.
        if (permission != null && !auth.Can(permission)) return (403, $"forbidden: missing permission {permission}");

        // Row 4: the host's resolver; its error text never reaches the client.
        PublicApiQuota quota;
        try
        {
            quota = await _options.ResolveQuotaAsync(caller, http.RequestServices, http.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested)
        {
            _logger.LogError(ex, "The public API quota resolver failed for user {UserId}", caller.UserId);
            return (500, InternalErrorMessage);
        }

        // Row 5.
        long? perDay = quota.RequestsPerDay is { } d ? Math.Max(0, d) : null;
        long? perMinute = quota.RequestsPerMinute is { } m ? Math.Max(0, m) : null;
        if (perDay == 0 || perMinute == 0) return (403, NoAccessMessage);

        // Rows 6-7: count, then check the account's sums, then take the count back when refused.
        var now = _time.GetUtcNow();
        var day = PublicApiWindows.Day(now);
        var minute = PublicApiWindows.Minute(now);
        var tokenId = caller.TokenId!;
        var ct = http.RequestAborted;
        var stripe = _admission[(int)((uint)StringComparer.Ordinal.GetHashCode(caller.UserId) % AdmissionStripes)];
        long dayCount, minuteCount;
        (int, string)? refusal = null;
        await stripe.WaitAsync(ct);
        try
        {
            // Not cancelled half-way: a count taken is always checked and, when refused, taken back.
            await _store.IncrementAsync(tokenId, caller.UserId, day, CancellationToken.None);
            await _store.IncrementAsync(tokenId, caller.UserId, minute, CancellationToken.None);
            dayCount = perDay != null || perMinute != null ? await _store.GetAccountCountAsync(caller.UserId, day, CancellationToken.None) : 0;
            minuteCount = perMinute != null ? await _store.GetAccountCountAsync(caller.UserId, minute, CancellationToken.None) : 0;
            if (perDay != null && dayCount > perDay)
                refusal = (429, $"quota exceeded: {perDay} requests per day");
            else if (perMinute != null && minuteCount > perMinute)
                refusal = (429, $"too many requests: {perMinute} per minute");
            if (refusal != null)
            {
                await _store.DecrementAsync(tokenId, day, CancellationToken.None);
                await _store.DecrementAsync(tokenId, minute, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The public API usage store failed for user {UserId}", caller.UserId);
            return (500, InternalErrorMessage);
        }
        finally
        {
            stripe.Release();
        }

        var toMidnight = SecondsUntil(now, now.UtcDateTime.Date.AddDays(1));
        var toNextMinute = SecondsUntil(now, new DateTime(now.UtcDateTime.Ticks - now.UtcDateTime.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(1));
        if (perDay != null)
            SetHeaders(http, perDay.Value, refusal != null ? 0 : perDay.Value - dayCount, toMidnight);
        else if (perMinute != null)
            SetHeaders(http, perMinute.Value, refusal != null ? 0 : perMinute.Value - minuteCount, toNextMinute);

        if (refusal is { } r)
        {
            var dayRefused = r.Item2.StartsWith("quota exceeded", StringComparison.Ordinal);
            http.Response.Headers.RetryAfter = (dayRefused ? toMidnight : toNextMinute).ToString(CultureInfo.InvariantCulture);
            return r;
        }

        await PruneIfDueAsync(now);
        return null;
    }

    /// <summary>Whole seconds from <paramref name="now"/> to <paramref name="next"/>, rounded up, at least 1.</summary>
    private static long SecondsUntil(DateTimeOffset now, DateTime next) =>
        Math.Max(1, (long)Math.Ceiling((next - now.UtcDateTime).TotalSeconds));

    private static void SetHeaders(HttpContext http, long limit, long remaining, long reset)
    {
        var headers = http.Response.Headers;
        headers["X-RateLimit-Limit"] = limit.ToString(CultureInfo.InvariantCulture);
        headers["X-RateLimit-Remaining"] = Math.Max(0, remaining).ToString(CultureInfo.InvariantCulture);
        headers["X-RateLimit-Reset"] = reset.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>At most once an hour: day counters older than the retention, minute counters older than 2 minutes.</summary>
    private async Task PruneIfDueAsync(DateTimeOffset now)
    {
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (last != long.MinValue && now.UtcTicks - last < TimeSpan.TicksPerHour) return;
        if (Interlocked.CompareExchange(ref _lastPruneTicks, now.UtcTicks, last) != last) return;
        try
        {
            await _store.PruneAsync(
                PublicApiWindows.Day(now.AddDays(-Math.Max(1, _options.UsageRetentionDays))),
                PublicApiWindows.Minute(now.AddMinutes(-2)),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pruning the public API usage counters failed");
        }
    }
}
