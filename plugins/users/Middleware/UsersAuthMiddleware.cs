using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Users.Middleware;

/// <summary>
/// Authenticates users-plugin routes and — with <see cref="UsersModuleOptions.ProtectServer"/> —
/// every CLI server route and WebSocket (see <see cref="CliRoutePolicy"/>). Sets a
/// <see cref="UsersAuthContext"/> and a core <see cref="CliPrincipal"/> on success.
/// <list type="bullet">
/// <item>HTTP: <c>Authorization: Bearer</c> token, or the external adapter.</item>
/// <item>WebSocket: single-use <c>?ticket=</c> from <c>POST /sessions/ws-ticket</c> (bearer tokens are never
/// accepted in query strings); in adapter mode the adapter may authenticate the upgrade directly
/// (a present Origin must then be allowed or same-origin).</item>
/// <item>CSRF: state-changing requests without a bearer token must not carry a disallowed
/// <c>Origin</c>; in adapter mode they must carry an allowed one. The request's own origin is always allowed.</item>
/// <item>Sessions with <c>mustChangePassword</c> may only read/end the current session and change their own password.</item>
/// </list>
/// </summary>
public class UsersAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<UsersAuthMiddleware> _logger;

    private const string Base = "/api/v1/qcli";
    private const string SessionsPath = Base + "/sessions";

    private static readonly string[] UsersPrefixes =
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
        var method = context.Request.Method;
        var options = context.RequestServices.GetRequiredService<UsersModuleOptions>();
        var serverOptions = context.RequestServices.GetService<CliServerOptions>() ?? new CliServerOptions();

        var isUsersRoute = IsUsersRoute(path);
        var rule = CliRoutePolicy.Resolve(method, path);
        // C1: with the users plugin the shell always needs a users session, even without ProtectServer.
        var isShellSocket = rule.IsWebSocket && rule.Permission == CliRoutePolicy.ShellExecute;
        if (!isUsersRoute && !isShellSocket && !(options.ProtectServer && rule.IsCliRoute))
        {
            await _next(context);
            return;
        }

        var bootstrap = context.RequestServices.GetService<UsersBootstrapService>();
        if (bootstrap != null)
            await bootstrap.EnsureSeededAsync();

        if (isUsersRoute && !LimitBody(context))
        {
            await WriteErrorAsync(context, 400, "request body too large");
            return;
        }

        var authService = context.RequestServices.GetRequiredService<AuthService>();
        var adapter = context.RequestServices.GetService<IExternalAuthAdapter>();
        var hasBearer = AuthService.BearerToken(context) != null;

        // B9: CSRF protection for state-changing requests that are not bearer-authenticated.
        if (IsStateChanging(method) && !hasBearer && !rule.IsWebSocket)
        {
            var origin = context.Request.Headers.Origin.FirstOrDefault();
            var originAllowed = serverOptions.IsOriginAllowed(origin, context.Request);
            if (!string.IsNullOrEmpty(origin) && serverOptions.AllowedOrigins.Count > 0 && !originAllowed)
            {
                await WriteErrorAsync(context, 403, "origin not allowed");
                return;
            }
            if (adapter != null && !rule.IsPublic && !IsCredentialEndpoint(method, path) && !originAllowed)
            {
                await WriteErrorAsync(context, 403, "origin not allowed");
                return;
            }
        }

        if (rule.IsPublic)
        {
            await _next(context);
            return;
        }

        // Credential endpoints answer 501 in external mode (handled by the controllers).
        if (adapter != null && IsCredentialEndpoint(method, path))
        {
            await _next(context);
            return;
        }

        UsersAuthContext? auth;
        try
        {
            if (rule.IsWebSocket)
            {
                auth = await authService.ConsumeTicketAsync(context.Request.Query["ticket"].FirstOrDefault());
                if (auth == null && adapter != null)
                {
                    // Adapter (cookie) auth rides along with cross-site upgrades: a present
                    // Origin must be allowed (or same-origin), otherwise this is CSWSH.
                    var origin = context.Request.Headers.Origin.FirstOrDefault();
                    if (!string.IsNullOrEmpty(origin) && !serverOptions.IsOriginAllowed(origin, context.Request))
                    {
                        await FailAsync(context, isShellSocket, 403, "origin not allowed");
                        return;
                    }
                    auth = await authService.AuthenticateRequestAsync(context, adapter);
                }
            }
            else
            {
                auth = await authService.AuthenticateRequestAsync(context, adapter);
            }
        }
        catch (UsersApiException ex)
        {
            _logger.LogDebug("Users auth failed for {Path}: {Reason}", path, ex.Message);
            await FailAsync(context, isShellSocket, ex.StatusCode, ex.Message);
            return;
        }

        if (auth == null)
        {
            await FailAsync(context, isShellSocket, 401, "unauthorized");
            return;
        }

        if (auth.User.MustChangePassword && !IsAllowedWhilePasswordChangeRequired(method, path, auth.User))
        {
            await FailAsync(context, isShellSocket, 403, AuthService.PasswordChangeRequiredMessage);
            return;
        }

        // The shell endpoint checks shell:execute itself and closes with 4403 (C5).
        if (!isUsersRoute && !isShellSocket && rule.Permission != null && !auth.Can(rule.Permission))
        {
            await WriteErrorAsync(context, 403, $"permission denied: {rule.Permission}");
            return;
        }

        context.SetUsersAuth(auth);
        var principal = auth.ToCliPrincipal();
        // Lets a shell opened by an adapter user be re-validated through the adapter later.
        if (rule.IsWebSocket && auth.ExternalRequest != null)
            authService.BindExternalRequest(principal, auth.ExternalRequest);
        context.SetCliPrincipal(principal);
        await _next(context);
    }

    /// <summary>
    /// Shell sockets are not rejected here: the failure is recorded and the shell endpoint accepts
    /// the upgrade and closes it with a readable code (4401/4403). Other requests get an HTTP error.
    /// </summary>
    private async Task FailAsync(HttpContext context, bool isShellSocket, int status, string message)
    {
        if (isShellSocket)
        {
            context.Items[CliAuthFailure.ItemKey] = new CliAuthFailure(status, message);
            await _next(context);
            return;
        }
        await WriteErrorAsync(context, status, message);
    }

    private static bool LimitBody(HttpContext context)
    {
        if (context.Request.ContentLength > UsersValidation.MaxBodyBytes) return false;
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = UsersValidation.MaxBodyBytes;
        return true;
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(
            System.Text.Json.JsonSerializer.Serialize(new ErrorResponse(message)));
    }

    private static bool IsStateChanging(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsDelete(method) || HttpMethods.IsPatch(method);

    /// <summary>GET/DELETE /sessions/current and PUT /users/{self}/password.</summary>
    internal static bool IsAllowedWhilePasswordChangeRequired(string method, string path, User user)
    {
        var trimmed = path.TrimEnd('/');
        if ((HttpMethods.IsGet(method) || HttpMethods.IsDelete(method)) &&
            string.Equals(trimmed, SessionsPath + "/current", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!HttpMethods.IsPut(method)) return false;
        var target = PasswordTarget(trimmed);
        return target != null &&
               (target == user.Id || string.Equals(target, user.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Logout, verify-password and password change: 501 in external mode.</summary>
    private static bool IsCredentialEndpoint(string method, string path)
    {
        var trimmed = path.TrimEnd('/');
        if (HttpMethods.IsDelete(method) && string.Equals(trimmed, SessionsPath + "/current", StringComparison.OrdinalIgnoreCase))
            return true;
        if (HttpMethods.IsPut(method) && PasswordTarget(trimmed) != null) return true;
        return HttpMethods.IsPost(method) && UserSubresource(trimmed, "verify-password") != null;
    }

    private static string? PasswordTarget(string trimmedPath) => UserSubresource(trimmedPath, "password");

    /// <summary>For <c>/users/{x}/{sub}</c> returns the unescaped <c>x</c>.</summary>
    private static string? UserSubresource(string trimmedPath, string sub)
    {
        const string prefix = Base + "/users/";
        if (!trimmedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var segments = trimmedPath[prefix.Length..].Split('/');
        return segments.Length == 2 && segments[1].Equals(sub, StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(segments[0])
            : null;
    }

    /// <summary>
    /// Segment-aware prefix match: "/api/v1/qcli/users" matches "/api/v1/qcli/users"
    /// and "/api/v1/qcli/users/..." but not "/api/v1/qcli/users-something".
    /// </summary>
    private static bool IsUsersRoute(string path)
    {
        foreach (var prefix in UsersPrefixes)
        {
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
