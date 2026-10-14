using Microsoft.AspNetCore.Http;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// <see cref="ICliAuthBridge"/> backed by the users plugin, so other plugins (e.g. the admin
/// dashboard) authenticate through users, sessions, lockout and audit.
/// </summary>
public sealed class UsersAuthBridge(AuthService auth, IpRateLimiter limiter, IExternalAuthAdapter? adapter = null) : ICliAuthBridge
{
    public async Task<CliAuthLogin> LoginAsync(HttpContext context, string username, string password)
    {
        if (adapter != null)
            throw new CliAuthException(501, "Authentication is managed by the host application");
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            throw new CliAuthException(400, "username and password are required");

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
}
