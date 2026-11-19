using System.Text.Json.Serialization;
using Qodalis.Cli.Plugin.Users.Controllers;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>What <see cref="IApiTokenManager.CreateAsync"/> creates.</summary>
public sealed class ApiTokenCreateOptions
{
    /// <summary>1-64 characters of <c>[A-Za-z0-9._ -]</c>, unique per user (case-insensitive, both audiences).</summary>
    public required string Name { get; init; }

    /// <summary>Lifetime in days (1-365); null = 90 days.</summary>
    public int? ExpiresInDays { get; init; }

    /// <summary>A token that never expires; needs <see cref="UsersModuleOptions.AllowNonExpiringTokens"/>.</summary>
    public bool NeverExpires { get; init; }

    /// <summary>
    /// The scope; null = the user's full permissions (<c>cli</c> tokens only). A <c>host</c> token needs at least
    /// one, each a <c>host</c> entry of the permission catalog.
    /// </summary>
    public IReadOnlyList<string>? Permissions { get; init; }

    /// <summary><see cref="ApiTokenAudience.Cli"/> (default) or <see cref="ApiTokenAudience.Host"/>.</summary>
    public string Audience { get; init; } = ApiTokenAudience.Cli;

    /// <summary>The name the audit log records as the actor; default the token owner's name.</summary>
    public string? Actor { get; init; }
}

/// <summary>A created token: the raw token (shown once) and its view, as the <c>POST /tokens</c> response.</summary>
public sealed class CreatedApiToken
{
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("apiToken")]
    public required ApiTokenView ApiToken { get; init; }
}

/// <summary>
/// API tokens the host application creates, lists and revokes for its users, with the validation, limits, audit
/// entries and events of the <c>/tokens</c> routes. Only the host creates <c>host</c> tokens: they carry permissions
/// the host declared with <c>cli.AddPermissions(...)</c> and are accepted only by its <c>[QcliAuthorize]</c> guards.
/// Errors are <see cref="UsersApiException"/>s with the status and text the HTTP route would answer; a null or empty
/// user id throws <see cref="ArgumentException"/>. The <c>CancellationToken</c> parameters are not observed yet: the
/// users plugin's stores take none.
/// </summary>
public interface IApiTokenManager
{
    /// <summary>Creates a token for <paramref name="userId"/>, checked against the user's live permissions.</summary>
    Task<CreatedApiToken> CreateAsync(string userId, ApiTokenCreateOptions options, CancellationToken ct = default);

    /// <summary>The user's tokens (both audiences), oldest first.</summary>
    Task<IReadOnlyList<ApiTokenView>> ListAsync(string userId, CancellationToken ct = default);

    /// <summary>Revokes a token of <paramref name="userId"/>; false when it does not exist or belongs to another user.</summary>
    Task<bool> RevokeAsync(string userId, string tokenId, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// The catalog entries the user may put on a token of <paramref name="audience"/>: for <c>host</c> the
    /// <c>host</c> entries the user holds, for <c>cli</c> every entry the user holds.
    /// </summary>
    Task<IReadOnlyList<CliPermission>> GetGrantablePermissionsAsync(string userId, string audience = ApiTokenAudience.Cli,
        CancellationToken ct = default);
}

/// <inheritdoc />
internal sealed class ApiTokenManager : IApiTokenManager
{
    private readonly AuthService _auth;
    private readonly IUserStore _users;
    private readonly ICliPermissionCatalog _catalog;
    private readonly UsersModuleOptions _options;

    public ApiTokenManager(AuthService auth, IUserStore users, ICliPermissionCatalog catalog, UsersModuleOptions options)
    {
        _auth = auth;
        _users = users;
        _catalog = catalog;
        _options = options;
    }

    public async Task<CreatedApiToken> CreateAsync(string userId, ApiTokenCreateOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentNullException.ThrowIfNull(options);
        if (!_options.ApiTokensEnabled)
            throw new UsersApiException(501, UsersApiFilterAttribute.ExternalAuthMessage);

        var user = await ActiveUserAsync(userId);
        if (!ApiTokenAudience.IsValid(options.Audience))
            throw UsersApiException.BadRequest(AuthService.InvalidAudienceMessage);

        var grantable = await _auth.GetEffectivePermissionsAsync(user);
        var actor = options.Actor ?? user.Name;
        var (token, apiToken) = await _auth.CreateApiTokenForAsync(user, grantable, options, actor,
            options.Actor == null ? user.Id : null, _catalog);
        return new CreatedApiToken { Token = token, ApiToken = AuthService.ToApiTokenView(apiToken, user.Name) };
    }

    public async Task<IReadOnlyList<ApiTokenView>> ListAsync(string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var tokens = await _auth.ListApiTokensAsync(userId);
        if (tokens.Count == 0) return [];
        var name = (await _users.GetUserAsync(userId))?.Name ?? userId;
        return tokens.Select(t => AuthService.ToApiTokenView(t, name)).ToList();
    }

    public async Task<bool> RevokeAsync(string userId, string tokenId, string? actor = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var token = (await _auth.ListApiTokensAsync(userId)).FirstOrDefault(t => t.Id == tokenId);
        if (token == null) return false;

        var owner = await _users.GetUserAsync(userId);
        var ownerName = owner?.Name ?? userId;
        await _auth.RevokeApiTokenCoreAsync(token, ownerName, actor ?? ownerName, actor == null ? owner?.Id : null);
        return true;
    }

    public async Task<IReadOnlyList<CliPermission>> GetGrantablePermissionsAsync(string userId,
        string audience = ApiTokenAudience.Cli, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var user = await _users.GetUserAsync(userId);
        if (user == null || user.Id != userId)
            throw UsersApiException.NotFound("user not found");
        if (!ApiTokenAudience.IsValid(audience))
            throw UsersApiException.BadRequest(AuthService.InvalidAudienceMessage);

        var held = await _auth.GetEffectivePermissionsAsync(user);
        return _catalog.All
            .Where(p => audience != ApiTokenAudience.Host || p.Surface == CliPermissionSurface.Host)
            .Where(p => PermissionMatcher.HasPermission(held, p.Name))
            .ToList();
    }

    /// <summary>The user by id: 404 when missing, 403 when disabled.</summary>
    private async Task<User> ActiveUserAsync(string userId)
    {
        // GetUserAsync also matches names; the manager takes ids only.
        var user = await _users.GetUserAsync(userId);
        if (user == null || user.Id != userId)
            throw UsersApiException.NotFound("user not found");
        if (user.Disabled)
            throw UsersApiException.Forbidden(AuthService.AccountDisabledMessage);
        return user;
    }
}
