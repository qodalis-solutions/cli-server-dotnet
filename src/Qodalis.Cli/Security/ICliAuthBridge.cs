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

    /// <summary>
    /// Re-validates the session behind <paramref name="principal"/> for a long-lived connection
    /// (an open shell): returns the principal with its current permissions, or null when the
    /// session ended (logout, expiry, revocation, user disabled or deleted).
    /// <paramref name="touch"/> counts the connection's activity as session activity.
    /// The default keeps the principal unchanged.
    /// </summary>
    Task<CliPrincipal?> RevalidateAsync(CliPrincipal principal, bool touch) =>
        Task.FromResult<CliPrincipal?>(principal);

    /// <summary>
    /// Records a shell audit event (<c>shell_opened</c> / <c>shell_closed</c>). Never receives the
    /// command text or stdin. The default does nothing.
    /// </summary>
    Task AuditShellAsync(CliShellAuditEvent auditEvent) => Task.CompletedTask;
}

/// <summary>A shell lifecycle event for <see cref="ICliAuthBridge.AuditShellAsync"/>.</summary>
/// <param name="Event"><c>shell_opened</c> or <c>shell_closed</c>.</param>
/// <param name="Principal">The user who owns the shell.</param>
/// <param name="Mode"><c>interactive</c> or <c>command</c>.</param>
/// <param name="RemoteAddress">Client address, when known.</param>
/// <param name="DurationMs">Shell lifetime (close only).</param>
/// <param name="ExitCode">Process exit code (close only, when the process exited).</param>
/// <param name="Reason">Why the shell closed (close only).</param>
public sealed record CliShellAuditEvent(
    string Event,
    CliPrincipal Principal,
    string Mode,
    string? RemoteAddress,
    long? DurationMs = null,
    int? ExitCode = null,
    string? Reason = null)
{
    public const string Opened = "shell_opened";
    public const string Closed = "shell_closed";
}

/// <summary>Result of <see cref="ICliAuthBridge.LoginAsync"/>.</summary>
public sealed record CliAuthLogin(string Token, CliPrincipal Principal, long ExpiresInSeconds);

/// <summary>Authentication failure with an HTTP status code.</summary>
public sealed class CliAuthException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
