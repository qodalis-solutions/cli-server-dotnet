using Microsoft.AspNetCore.Http;

namespace Qodalis.Cli.Security;

/// <summary>
/// Bridge to the installed authentication plugin (the users plugin registers one).
/// Lets other plugins (e.g. the admin dashboard) authenticate through it.
/// </summary>
public interface ICliAuthBridge
{
    /// <summary>
    /// Verifies credentials and creates a session. Throws <see cref="CliAuthException"/> on failure
    /// (401 invalid credentials, 403 disabled, 429 locked/rate limited, 501 external auth).
    /// </summary>
    Task<CliAuthLogin> LoginAsync(HttpContext context, string username, string password);

    /// <summary>
    /// Authenticates the request (bearer token or external adapter); null when unauthenticated.
    /// Throws <see cref="CliAuthException"/> (403) when the session must change its password first.
    /// </summary>
    Task<CliPrincipal?> AuthenticateAsync(HttpContext context);

    /// <summary>Ends the session identified by <paramref name="token"/>.</summary>
    Task LogoutAsync(HttpContext context, string token);
}

/// <summary>Result of <see cref="ICliAuthBridge.LoginAsync"/>.</summary>
public sealed record CliAuthLogin(string Token, CliPrincipal Principal, long ExpiresInSeconds);

/// <summary>Authentication failure with an HTTP status code.</summary>
public sealed class CliAuthException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
