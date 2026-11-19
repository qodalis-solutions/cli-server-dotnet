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
    internal static readonly (int, string) ClientGone = (StatusCodes.Status499ClientClosedRequest, string.Empty);
    /// <summary>The <c>HttpContext.Items</c> key of an admitted request; its value is the admitted <see cref="UsersAuthContext"/>.</summary>
    internal static readonly object AdmittedKey = new();
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

    /// <summary>
    /// Null when the request is admitted (headers set, request counted); otherwise the refusal. Status
    /// <see cref="StatusCodes.Status499ClientClosedRequest"/> means the client went away: the caller ends the request
    /// without a body.
    /// </summary>
    public async Task<(int Status, string Error)?> CheckAsync(HttpContext http, string? permission)
    {
        // Stacked guards (a route group and its endpoint, a controller and its action): the first one admitted and
        // counted the request; the others check only their own permission and never count again.
        // A marker without the admitted caller (something else wrote the key) runs the full check.
        if (http.Items.TryGetValue(AdmittedKey, out var marker) && marker is UsersAuthContext admitted &&
            ReferenceEquals(admitted, http.GetUsersAuth()) && http.GetQcliCaller() != null)
            return permission != null && !admitted.Can(permission) ? MissingPermission(permission) : null;

        // Rows 1-2: authentication and the host-token check (the users guard's texts). The caller is admitted
        // (GetQcliCaller, HttpContext.User) only once every check below passed.
        if (await QcliAuthorization.CheckAsync(http, null, hostTokenOnly: true) is { } failure) return failure;
        var auth = http.GetUsersAuth()!;
        var caller = auth.ToQcliCaller();

        // Row 3: this guard's permission, then those of the other public-api guards on the endpoint, so a request a
        // later guard refuses is not counted.
        foreach (var required in RequiredPermissions(http, permission))
        {
            if (!auth.Can(required)) return MissingPermission(required);
        }

        // Row 4: the host's resolver; its error text never reaches the client.
        PublicApiQuota quota;
        try
        {
            quota = await _options.ResolveQuotaAsync(caller, http.RequestServices, http.RequestAborted);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return ClientGone;
        }
        catch (Exception ex)
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
        var stripe = _admission[(int)((uint)StringComparer.Ordinal.GetHashCode(caller.UserId) % AdmissionStripes)];
        long dayCount = 0, minuteCount = 0;
        string? refusal = null;
        var dayRefused = false;
        try
        {
            await stripe.WaitAsync(http.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            return ClientGone;
        }
        var counted = new List<string>(2);
        try
        {
            // Not cancelled half-way: a count taken is always checked and, when refused or failed, taken back.
            await _store.IncrementAsync(tokenId, caller.UserId, day, CancellationToken.None);
            counted.Add(day);
            await _store.IncrementAsync(tokenId, caller.UserId, minute, CancellationToken.None);
            counted.Add(minute);
            if (perDay != null || perMinute != null)
                dayCount = await _store.GetAccountCountAsync(caller.UserId, day, CancellationToken.None);
            if (perMinute != null)
                minuteCount = await _store.GetAccountCountAsync(caller.UserId, minute, CancellationToken.None);
            if (perDay != null && dayCount > perDay)
            {
                refusal = $"quota exceeded: {perDay} requests per day";
                dayRefused = true;
            }
            else if (perMinute != null && minuteCount > perMinute)
            {
                refusal = $"too many requests: {perMinute} per minute";
            }
            if (refusal != null) await UncountAsync(tokenId, counted, caller.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The public API usage store failed for user {UserId}", caller.UserId);
            await UncountAsync(tokenId, counted, caller.UserId);
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

        if (refusal != null)
        {
            http.Response.Headers.RetryAfter = (dayRefused ? toMidnight : toNextMinute).ToString(CultureInfo.InvariantCulture);
            return (429, refusal);
        }

        QcliAuthorization.Admit(http);
        http.Items[AdmittedKey] = auth;
        await PruneIfDueAsync(now);
        return null;
    }

    private static (int, string) MissingPermission(string permission) => (403, $"forbidden: missing permission {permission}");

    /// <summary>
    /// <paramref name="own"/>, then the permissions of every public-api guard on the endpoint
    /// (<see cref="PublicApiEndpointMetadata"/>, <see cref="RequirePublicApiAttribute"/>), in metadata order, once each.
    /// </summary>
    private static IEnumerable<string> RequiredPermissions(HttpContext http, string? own)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (own != null && seen.Add(own)) yield return own;
        var metadata = http.GetEndpoint()?.Metadata;
        if (metadata == null) yield break;
        foreach (var item in metadata)
        {
            var required = item switch
            {
                PublicApiEndpointMetadata e => e.Permission,
                RequirePublicApiAttribute a => a.Permission,
                _ => null,
            };
            if (required != null && seen.Add(required)) yield return required;
        }
    }

    /// <summary>Takes back the counts this request took (a refusal or a store failure); best effort.</summary>
    private async Task UncountAsync(string tokenId, List<string> windows, string userId)
    {
        foreach (var window in windows)
        {
            try
            {
                await _store.DecrementAsync(tokenId, window, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Taking back a public API count failed for user {UserId}", userId);
            }
        }
        windows.Clear();
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
