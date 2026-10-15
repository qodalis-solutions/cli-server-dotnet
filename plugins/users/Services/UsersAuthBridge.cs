using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// <see cref="ICliAuthBridge"/> backed by the users plugin, so other plugins (e.g. the admin
/// dashboard) authenticate through users, sessions, lockout and audit.
/// </summary>
public sealed class UsersAuthBridge(AuthService auth, IpRateLimiter limiter, IExternalAuthAdapter? adapter = null,
    IServiceProvider? services = null) : ICliAuthBridge
{
    public async Task<CliAuthLogin> LoginAsync(HttpContext context, string username, string password)
    {
        if (adapter != null)
            throw new CliAuthException(501, "Authentication is managed by the host application");
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            throw new CliAuthException(400, "username and password are required");
        if (username.Length > UsersValidation.MaxEmailLength || password.Length > auth.Options.MaxPasswordLength)
            throw new CliAuthException(400, "username or password is too long");

        try
        {
            limiter.Acquire(context);
            var metadata = new SessionMetadata
            {
                RemoteAddress = limiter.ClientIp(context),
                UserAgent = UsersValidation.Sanitize(context.Request.Headers.UserAgent.FirstOrDefault(), 512),
            };
            var result = await auth.LoginAsync(username, password, metadata);
            var principal = new CliPrincipal(result.Session.User.Id, result.Session.User.Name,
                result.Session.Permissions, result.Session.Id);
            return new CliAuthLogin(result.Token, principal, auth.SessionTimeoutMs / 1000);
        }
        catch (UsersApiException ex)
        {
            throw new CliAuthException(ex.StatusCode, ex.Message);
        }
    }

    public async Task<CliPrincipal?> AuthenticateAsync(HttpContext context)
    {
        var existing = context.GetUsersAuth();
        if (existing != null)
        {
            if (existing.User.MustChangePassword)
                throw new CliAuthException(403, AuthService.PasswordChangeRequiredMessage);
            return existing.ToCliPrincipal();
        }

        try
        {
            var ctx = await auth.AuthenticateRequestAsync(context, adapter);
            if (ctx == null) return null;
            if (ctx.User.MustChangePassword)
                throw new CliAuthException(403, AuthService.PasswordChangeRequiredMessage);
            return ctx.ToCliPrincipal();
        }
        catch (UsersApiException)
        {
            return null;
        }
    }

    public async Task LogoutAsync(HttpContext context, string token) => await auth.LogoutAsync(token);

    /// <summary>
    /// Stored sessions are re-validated against the stores (current permissions, expiry, user state).
    /// External-adapter principals are re-validated by running the adapter again on the request it
    /// accepted (the shell upgrade, or the ticket request): no user, another user or an exception
    /// ends the session. Adapter principals without a recorded request cannot be re-validated (null).
    /// </summary>
    public async Task<CliPrincipal?> RevalidateAsync(CliPrincipal principal, bool touch)
    {
        if (principal.SessionId == null) return null;
        if (principal.SessionId.StartsWith("external-", StringComparison.Ordinal))
        {
            if (adapter == null) return null;
            using var scope = services?.CreateScope();
            var external = await auth.RevalidateExternalAsync(principal, adapter, scope?.ServiceProvider);
            if (external?.ExternalRequest == null) return null;
            var current = external.ToCliPrincipal();
            auth.BindExternalRequest(current, external.ExternalRequest);
            return current;
        }
        var ctx = await auth.RevalidateSessionAsync(principal.SessionId, touch);
        return ctx?.ToCliPrincipal();
    }

    /// <summary>Records <c>shell_opened</c> / <c>shell_closed</c> (never the command text or stdin).</summary>
    public Task AuditShellAsync(CliShellAuditEvent e) =>
        auth.AuditAsync(e.Event, e.Principal.UserName,
            e.Event == CliShellAuditEvent.Opened ? "shell opened" : "shell closed",
            actor: e.Principal.UserName,
            metadata: new AuditMetadata
            {
                RemoteAddress = e.RemoteAddress,
                SessionId = e.Principal.SessionId,
                Mode = e.Mode,
                DurationMs = e.DurationMs,
                ExitCode = e.ExitCode,
                Reason = e.Reason,
            },
            userId: e.Principal.UserId,
            actorId: e.Principal.UserId);
}
