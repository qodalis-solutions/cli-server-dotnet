using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;

namespace Qodalis.Cli.Plugin.Users.Authorization;

/// <summary>
/// The caller of a guarded host route (<c>[QcliAuthorize]</c>, <c>.RequireQcliAuthorization(...)</c> or the public-api
/// guard), read with <see cref="QcliCallerHttpContextExtensions.GetQcliCaller"/>.
/// </summary>
/// <param name="UserId">The user's id.</param>
/// <param name="UserName">The user's name.</param>
/// <param name="TokenId">The API token's id; null for a session or an external-adapter caller.</param>
/// <param name="Audience"><c>cli</c> or <c>host</c> for a token (anything not exactly <c>cli</c> reads as <c>host</c>); null otherwise.</param>
/// <param name="Permissions">The live effective permissions, narrowed by the token's scope.</param>
public sealed record QcliCaller(
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("userName")] string UserName,
    [property: JsonPropertyName("tokenId")] string? TokenId,
    [property: JsonPropertyName("audience")] string? Audience,
    [property: JsonPropertyName("permissions")] IReadOnlyList<string> Permissions);

/// <summary>Reads the caller a users-plugin guard admitted.</summary>
public static class QcliCallerHttpContextExtensions
{
    internal const string ItemKey = "Qodalis.Cli.Plugin.Users.Caller";

    /// <summary>The caller a guard admitted on this request, or null when no guard ran (or it refused).</summary>
    public static QcliCaller? GetQcliCaller(this HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) ? value as QcliCaller : null;

    internal static void SetQcliCaller(this HttpContext context, QcliCaller caller) => context.Items[ItemKey] = caller;

    /// <summary>The caller of an authenticated request.</summary>
    public static QcliCaller ToQcliCaller(this UsersAuthContext auth) => new(
        auth.User.Id,
        auth.User.Name,
        auth.ApiToken?.Id,
        auth.ApiToken == null ? null : auth.IsHostToken ? ApiTokenAudience.Host : ApiTokenAudience.Cli,
        [.. auth.Permissions]);
}
