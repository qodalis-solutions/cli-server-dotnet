using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.Middleware;

/// <summary>
/// Middleware that validates Bearer tokens for users plugin routes and
/// stores the session in <see cref="HttpContext.Items"/>.
/// When an <see cref="IExternalAuthAdapter"/> is registered, authentication
/// is delegated to it instead of using the built-in token scheme.
/// </summary>
public class UsersAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<UsersAuthMiddleware> _logger;

    private static readonly string[] ProtectedPrefixes =
    [
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

        // Check if an external auth adapter is registered
        var adapter = context.RequestServices.GetService<IExternalAuthAdapter>();

        if (adapter != null)
        {
            await InvokeWithExternalAuth(context, path, adapter);
        }
        else
        {
            await InvokeWithBuiltInAuth(context, path);
        }
    }

    private async Task InvokeWithExternalAuth(HttpContext context, string path, IExternalAuthAdapter adapter)
    {
        // Skip non-plugin routes
        if (!IsProtectedRoute(path))
        {
            await _next(context);
            return;
        }

        // Let login/logout through to the controller (which returns 501 in external mode)
        if (IsLogin(context, path) || IsLogout(context, path))
        {
            await _next(context);
            return;
        }

        var result = adapter.Authenticate(context);
        if (result == null)
        {
            _logger.LogWarning("External auth adapter returned null for {Path}", path);
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
            return;
        }

        // Build a lightweight session from the adapter result
        var groups = result.User.Groups ?? [];
        if (result.IsAdmin && !groups.Contains("admin"))
        {
            groups = [.. groups, "admin"];
        }

        var session = new Session
        {
            Id = $"external-{Guid.NewGuid():N}",
            Token = string.Empty,
            UserId = result.User.Id,
            User = new User
            {
                Id = result.User.Id,
                Name = result.User.Name,
                Email = result.User.Email,
                Groups = groups,
                Data = result.User.Data,
            },
            LoginTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastActivity = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        context.Items["UsersSession"] = session;
        await _next(context);
    }

    private async Task InvokeWithBuiltInAuth(HttpContext context, string path)
    {
        // Login (POST /sessions) is the only unauthenticated users-plugin endpoint
        if (IsLogin(context, path))
        {
            await _next(context);
            return;
        }

        // Only protect users-plugin routes
        if (!IsProtectedRoute(path))
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

    private const string SessionsPath = "/api/v1/qcli/sessions";

    /// <summary>POST /api/v1/qcli/sessions — login.</summary>
    private static bool IsLogin(HttpContext context, string path) =>
        HttpMethods.IsPost(context.Request.Method) && IsExactly(path, SessionsPath);

    /// <summary>DELETE /api/v1/qcli/sessions/current — logout.</summary>
    private static bool IsLogout(HttpContext context, string path) =>
        HttpMethods.IsDelete(context.Request.Method) && IsExactly(path, SessionsPath + "/current");

    private static bool IsExactly(string path, string expected) =>
        string.Equals(path.TrimEnd('/'), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Segment-aware prefix match: "/api/v1/qcli/users" matches "/api/v1/qcli/users"
    /// and "/api/v1/qcli/users/..." but not "/api/v1/qcli/users-something".
    /// </summary>
    private static bool IsProtectedRoute(string path)
    {
        foreach (var prefix in ProtectedPrefixes)
        {
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
