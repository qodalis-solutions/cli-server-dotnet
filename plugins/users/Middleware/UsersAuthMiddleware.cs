using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.Middleware;

/// <summary>
/// Middleware that validates Bearer tokens for users plugin routes and
/// stores the session in <see cref="HttpContext.Items"/>.
/// </summary>
public class UsersAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<UsersAuthMiddleware> _logger;

    private static readonly string[] ProtectedPrefixes =
    [
        "/api/v1/qcli/auth",
        "/api/v1/qcli/users",
        "/api/v1/qcli/sessions",
        "/api/v1/qcli/groups",
        "/api/v1/qcli/audit",
    ];

    public UsersAuthMiddleware(RequestDelegate next, ILogger<UsersAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Skip unauthenticated auth endpoints
        if (path.StartsWith("/api/v1/qcli/auth/login", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/qcli/auth/verify", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Only protect users-plugin routes
        var isProtected = false;
        foreach (var prefix in ProtectedPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                isProtected = true;
                break;
            }
        }

        if (!isProtected)
        {
            await _next(context);
            return;
        }

        // Validate bearer token
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Users auth failed for {Path}: missing authorization header", path);
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
            return;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        var sessionStore = context.RequestServices.GetRequiredService<ISessionStore>();
        var session = await sessionStore.GetSessionAsync(token);

        if (session == null)
        {
            _logger.LogWarning("Users auth failed for {Path}: invalid or expired token", path);
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired token" });
            return;
        }

        await sessionStore.UpdateLastActivityAsync(token);
        context.Items["UsersSession"] = session;

        await _next(context);
    }
}
