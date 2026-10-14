using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;

namespace Qodalis.Cli.Plugin.Users.Middleware;

/// <summary>
/// Authenticates users-plugin routes and stores a <see cref="UsersAuthContext"/> in
/// <see cref="HttpContext.Items"/>. Built-in mode validates the Bearer token (idle timeout,
/// live user resolution); external mode delegates to the registered
/// <see cref="IExternalAuthAdapter"/>. Controllers fail closed when no context is set.
/// </summary>
public class UsersAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<UsersAuthMiddleware> _logger;

    private const string Base = "/api/v1/qcli";
    private const string SessionsPath = Base + "/sessions";

    private static readonly string[] ProtectedPrefixes =
    [
        Base + "/users",
        Base + "/sessions",
        Base + "/groups",
        Base + "/audit",
    ];

    public UsersAuthMiddleware(RequestDelegate next, ILogger<UsersAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!IsProtectedRoute(path))
        {
            await _next(context);
            return;
        }

        var bootstrap = context.RequestServices.GetService<UsersBootstrapService>();
        if (bootstrap != null)
            await bootstrap.EnsureSeededAsync();

        // Login is the only anonymous endpoint.
        if (IsLogin(context, path))
        {
            await _next(context);
            return;
        }

        var authService = context.RequestServices.GetRequiredService<AuthService>();
        var adapter = context.RequestServices.GetService<IExternalAuthAdapter>();

        if (adapter != null)
        {
            // Credential endpoints answer 501 in external mode (handled by the controllers).
            if (IsCredentialEndpoint(context, path))
            {
                await _next(context);
                return;
            }

            var result = adapter.Authenticate(context);
            if (result?.User == null)
            {
                _logger.LogDebug("External auth adapter rejected {Path}", path);
                await WriteErrorAsync(context, 401, "unauthorized");
                return;
            }

            context.SetUsersAuth(await authService.AuthenticateExternalAsync(result));
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context, 401, "unauthorized");
            return;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        try
        {
            context.SetUsersAuth(await authService.AuthenticateTokenAsync(token));
        }
        catch (UsersApiException ex)
        {
            _logger.LogDebug("Users auth failed for {Path}: {Reason}", path, ex.Message);
            await WriteErrorAsync(context, ex.StatusCode, ex.Message);
            return;
        }

        await _next(context);
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(
            System.Text.Json.JsonSerializer.Serialize(new ErrorResponse(message)));
    }

    /// <summary>POST /sessions — login.</summary>
    private static bool IsLogin(HttpContext context, string path) =>
        HttpMethods.IsPost(context.Request.Method) && IsExactly(path, SessionsPath);

    /// <summary>Logout, verify-password and password change: 501 in external mode.</summary>
    private static bool IsCredentialEndpoint(HttpContext context, string path)
    {
        var trimmed = path.TrimEnd('/');
        if (HttpMethods.IsDelete(context.Request.Method) && IsExactly(trimmed, SessionsPath + "/current"))
            return true;

        if (!trimmed.StartsWith(Base + "/users/", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = trimmed[(Base.Length + "/users/".Length)..].Split('/');
        if (segments.Length != 2) return false;

        return (HttpMethods.IsPost(context.Request.Method) &&
                segments[1].Equals("verify-password", StringComparison.OrdinalIgnoreCase)) ||
               (HttpMethods.IsPut(context.Request.Method) &&
                segments[1].Equals("password", StringComparison.OrdinalIgnoreCase));
    }

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
