using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Qodalis.Cli.Plugin.Admin.Auth;

/// <summary>
/// ASP.NET Core middleware that validates JWT Bearer tokens for admin API routes.
/// Skips auth for the login endpoint and static admin files.
/// </summary>
public class AdminAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly JwtService _jwtService;
    private readonly ILogger<AdminAuthMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminAuthMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="jwtService">The JWT service for token validation.</param>
    /// <param name="logger">The logger instance.</param>
    public AdminAuthMiddleware(RequestDelegate next, JwtService jwtService, ILogger<AdminAuthMiddleware> logger)
    {
        _next = next;
        _jwtService = jwtService;
        _logger = logger;
    }

    /// <summary>
    /// Validates the JWT Bearer token for admin API routes and passes through non-admin requests.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    public async Task Invoke(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Only protect /api/v1/qcli/ admin routes
        if (!path.StartsWith("/api/v1/qcli/", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Path {Path} is not an admin route, skipping auth", path);
            await _next(context);
            return;
        }

        // Skip auth for login endpoint
        if (path.Contains("/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Path {Path} is the login endpoint, skipping auth", path);
            await _next(context);
            return;
        }

        // Skip auth for non-admin qcli routes (commands, execute, version, etc.)
        // Only protect admin-specific routes
        if (!IsAdminRoute(path))
        {
            _logger.LogDebug("Path {Path} is not a protected admin route, skipping auth", path);
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Admin auth failed for {Path}: missing or invalid authorization header", path);
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid authorization header", code = "UNAUTHORIZED" });
            return;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        var principal = _jwtService.ValidateToken(token);

        if (principal == null)
        {
            _logger.LogWarning("Admin auth failed for {Path}: invalid or expired token", path);
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired token", code = "UNAUTHORIZED" });
            return;
        }

        _logger.LogDebug("Admin auth succeeded for {Path}", path);
        context.Items["AdminUser"] = principal;
        await _next(context);
    }

    private static bool IsAdminRoute(string path)
    {
        // Admin-specific routes that require authentication
        var adminPrefixes = new[]
        {
            "/api/v1/qcli/auth/me",
            "/api/v1/qcli/status",
            "/api/v1/qcli/plugins",
            "/api/v1/qcli/config",
            "/api/v1/qcli/logs",
            "/api/v1/qcli/ws/clients",
        };

        foreach (var prefix in adminPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
