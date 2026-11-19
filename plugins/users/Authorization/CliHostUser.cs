using System.Security.Claims;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Users.Authorization;

/// <summary>
/// The ASP.NET user (<c>HttpContext.User</c>) the users plugin sets for a CLI request it authenticated itself
/// (session, API token or WebSocket ticket), so host code that reads the signed-in user the usual way sees the
/// caller. See <see cref="UsersModuleOptions.SetHostUser"/>.
/// </summary>
public static class CliHostUser
{
    /// <summary>The identity's authentication type: tells host code the user came through the CLI.</summary>
    public const string AuthenticationType = "QodalisCli";

    /// <summary>One claim per effective CLI permission (<c>resource:action</c>).</summary>
    public const string PermissionClaim = "qcli:permission";

    /// <summary>How the caller signed in: <c>token</c> (API token) or <c>session</c>.</summary>
    public const string MethodClaim = "qcli:auth";

    /// <summary>The API token's id; only for requests authenticated by a token.</summary>
    public const string TokenClaim = "qcli:token";

    /// <summary>The API token's audience, <c>cli</c> or <c>host</c>; only for requests authenticated by a token.</summary>
    public const string AudienceClaim = "qcli:audience";

    /// <summary>
    /// Builds the host user from the CLI principal; the user id is the one the store (or the external adapter) gave.
    /// With <paramref name="token"/> (the API token that authenticated the request) the token id and audience are added.
    /// </summary>
    public static ClaimsPrincipal Create(CliPrincipal principal, string? email, ApiToken? token = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, principal.UserId),
            new("sub", principal.UserId),
            new(ClaimTypes.Name, principal.UserName),
            new(MethodClaim, principal.TokenId != null ? "token" : "session"),
        };
        if (!string.IsNullOrEmpty(email))
            claims.Add(new Claim(ClaimTypes.Email, email));
        if (token != null)
        {
            claims.Add(new Claim(TokenClaim, token.Id));
            // Fail closed as everywhere else: anything not exactly cli is a host token.
            claims.Add(new Claim(AudienceClaim, token.Audience == ApiTokenAudience.Cli ? ApiTokenAudience.Cli : ApiTokenAudience.Host));
        }
        claims.AddRange(principal.Permissions.Select(p => new Claim(PermissionClaim, p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType, ClaimTypes.Name, ClaimTypes.Role));
    }
}
