using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Plugin.Users.Authorization;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.PublicApi;

/// <summary>An account's public API usage, for the host's own pages.</summary>
public sealed class PublicApiAccountUsage
{
    /// <summary>The account's requests today (UTC), over every token, revoked ones included.</summary>
    [JsonPropertyName("today")]
    public long Today { get; init; }

    /// <summary>The account's quota, from the host's resolver (<see cref="PublicApiQuota.Unlimited"/> without one).</summary>
    [JsonPropertyName("quota")]
    public required PublicApiQuota Quota { get; init; }

    /// <summary>The account's current tokens (both audiences) and their usage.</summary>
    [JsonPropertyName("tokens")]
    public required IReadOnlyList<PublicApiTokenUsage> Tokens { get; init; }
}

/// <summary>One token's usage.</summary>
public sealed record PublicApiTokenUsage(
    [property: JsonPropertyName("tokenId")] string TokenId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("usage")] ApiTokenUsage Usage);

/// <summary>Usage of the public API by account (for a host's account page).</summary>
public interface IPublicApiUsage
{
    /// <summary>
    /// The account's usage today, its quota and its tokens' last 30 days. An unknown user throws
    /// <see cref="UsersApiException"/> 404 <c>user not found</c>.
    /// </summary>
    Task<PublicApiAccountUsage> GetAccountUsageAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IPublicApiUsage"/>, and the users plugin's <see cref="IApiTokenUsageProvider"/>: the <c>usage</c> of
/// token views, 30 dense UTC days ending today.
/// </summary>
internal sealed class PublicApiUsageService : IPublicApiUsage, IApiTokenUsageProvider
{
    private const int Days = 30;

    private readonly IPublicApiUsageStore _store;
    private readonly TimeProvider _time;
    private readonly PublicApiUsageOptions _options;
    private readonly IServiceScopeFactory _scopes;

    public PublicApiUsageService(IPublicApiUsageStore store, TimeProvider time, PublicApiUsageOptions options, IServiceScopeFactory scopes)
    {
        _store = store;
        _time = time;
        _options = options;
        _scopes = scopes;
    }

    public async Task<IReadOnlyDictionary<string, ApiTokenUsage>> GetUsageAsync(IReadOnlyCollection<string> tokenIds,
        CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var dates = Enumerable.Range(0, Days)
            .Select(i => today.AddDays(i - (Days - 1)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        var stored = tokenIds.Count == 0
            ? new Dictionary<string, IReadOnlyDictionary<string, long>>()
            : await _store.GetTokenDaysAsync(tokenIds, dates[0], dates[^1], ct);

        var result = new Dictionary<string, ApiTokenUsage>();
        foreach (var id in tokenIds.Distinct())
        {
            stored.TryGetValue(id, out var counts);
            var days = dates.Select(d => new ApiTokenUsageDay(d, counts != null && counts.TryGetValue(d, out var c) ? c : 0)).ToList();
            result[id] = new ApiTokenUsage { Today = days[^1].Requests, Last30Days = days.Sum(d => d.Requests), Days = days };
        }
        return result;
    }

    public async Task<PublicApiAccountUsage> GetAccountUsageAsync(string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        await using var scope = _scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var user = await services.GetRequiredService<IUserStore>().GetUserAsync(userId);
        if (user == null || user.Id != userId) throw UsersApiException.NotFound("user not found");

        var permissions = await services.GetRequiredService<AuthService>().GetEffectivePermissionsAsync(user);
        var caller = new QcliCaller(user.Id, user.Name, null, null, permissions);
        var quota = await _options.ResolveQuotaAsync(caller, services, ct);

        var tokens = await services.GetRequiredService<IApiTokenStore>().GetTokensAsync(userId);
        var usage = await GetUsageAsync(tokens.Select(t => t.Id).ToList(), ct);
        return new PublicApiAccountUsage
        {
            Today = await _store.GetAccountCountAsync(userId, PublicApiWindows.Day(_time.GetUtcNow()), ct),
            Quota = quota,
            Tokens = tokens.Select(t => new PublicApiTokenUsage(t.Id, t.Name, usage[t.Id])).ToList(),
        };
    }
}
