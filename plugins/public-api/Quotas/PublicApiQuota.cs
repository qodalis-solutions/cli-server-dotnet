using System.Text.Json.Serialization;
using Qodalis.Cli.Plugin.Users.Authorization;

namespace Qodalis.Cli.Plugin.PublicApi;

/// <summary>
/// An account's limits on the public API. <c>null</c> = no limit of that kind; <c>0</c> (or a negative value) = no
/// access at all (403 <c>forbidden: no API access</c>). Windows are fixed UTC days and minutes; the limits apply to the
/// sum over every token of the account, revoked tokens included.
/// </summary>
/// <param name="RequestsPerDay">Requests per UTC day.</param>
/// <param name="RequestsPerMinute">Requests per UTC minute.</param>
public sealed record PublicApiQuota(
    [property: JsonPropertyName("requestsPerDay")] long? RequestsPerDay,
    [property: JsonPropertyName("requestsPerMinute")] long? RequestsPerMinute)
{
    /// <summary>No limits.</summary>
    public static readonly PublicApiQuota Unlimited = new(null, null);
}

/// <summary>
/// The host's quota for a caller (its plan, its account's settings, ...). Resolved from the request's services for
/// every guarded request, so scoped services (a <c>DbContext</c>) work. Without a resolver the API is unlimited.
/// An exception answers 500 <c>internal server error</c> (logged, never shown) and the request is not counted.
/// </summary>
public interface IPublicApiQuotaResolver
{
    /// <summary>The quota of <paramref name="caller"/>'s account.</summary>
    ValueTask<PublicApiQuota> GetQuotaAsync(QcliCaller caller, CancellationToken ct);
}
