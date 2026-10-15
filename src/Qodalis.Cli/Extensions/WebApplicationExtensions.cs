using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Extensions;

/// <summary>
/// Extension methods for <see cref="IApplicationBuilder"/> to configure CLI WebSocket middleware.
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>
    /// Adds CLI middleware for WebSocket endpoints (events, logs and — when enabled — shell).
    /// Must be called after <c>UseWebSockets()</c>. Upgrades are checked against
    /// <see cref="CliServerOptions.AllowedOrigins"/> and, when the server is protected, require
    /// a <see cref="CliPrincipal"/> established by the authentication plugin.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseCli(this IApplicationBuilder app)
    {
        LogShellConfiguration(app.ApplicationServices);

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value;
            var kind = path switch
            {
                "/ws/v1/qcli/events" or "/ws/qcli/events" => "events",
                "/ws/v1/qcli/shell" or "/ws/qcli/shell" => "shell",
                "/ws/v1/qcli/logs" or "/ws/qcli/logs" => "logs",
                "/ws/v1/qcli" or "/ws/qcli" => "terminal",
                _ => null,
            };

            if (kind == null)
            {
                await next();
                return;
            }

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Qodalis.Cli.WebSocketMiddleware");
            var options = context.RequestServices.GetService<CliServerOptions>() ?? new CliServerOptions();
            var isShell = kind is "shell" or "terminal";

            if (isShell && !options.EnableShell && !context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                logger.LogWarning("Non-WebSocket request to WebSocket endpoint: {Path}", path);
                context.Response.StatusCode = 400;
                return;
            }

            var origin = context.Request.Headers.Origin.FirstOrDefault();
            if (options.AllowedOrigins.Count > 0 && !string.IsNullOrEmpty(origin) && !options.IsOriginAllowed(origin))
            {
                logger.LogWarning("WebSocket upgrade rejected for {Path}: origin not allowed", path);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (isShell)
            {
                await HandleShellAsync(context, options, logger);
                return;
            }

            var principal = context.GetCliPrincipal();
            if (options.RequireAuthentication)
            {
                var rule = CliRoutePolicy.Resolve(context.Request.Method, path);
                if (principal == null)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                if (rule.Permission != null && !principal.Can(rule.Permission))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }

            var remoteAddress = context.Connection.RemoteIpAddress?.ToString();
            switch (kind)
            {
                case "events":
                {
                    logger.LogDebug("Event WebSocket connection accepted");
                    var manager = context.RequestServices.GetRequiredService<ICliEventSocketManager>();
                    var socket = await context.WebSockets.AcceptWebSocketAsync();
                    await manager.HandleConnectionAsync(socket, context.RequestAborted, remoteAddress, principal);
                    break;
                }
                case "logs":
                {
                    var logManager = context.RequestServices.GetRequiredService<ICliLogSocketManager>();
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    var levelFilter = context.Request.Query["level"].FirstOrDefault();
                    logger.LogDebug("Log WebSocket connection accepted (level={Level})", levelFilter ?? "all");
                    await logManager.HandleConnectionAsync(webSocket, levelFilter, context.RequestAborted);
                    break;
                }
            }
        });

        return app;
    }

    /// <summary>
    /// Shell sockets (C1/C5): the upgrade is accepted and failures are reported with a close code
    /// (4404 disabled, 4401 login required/locked, 4403 forbidden) since browsers cannot read the
    /// HTTP status of a failed upgrade. Limits (4429) and timeouts (4408) are enforced by
    /// <see cref="IShellSessionManager"/>.
    /// </summary>
    private static async Task HandleShellAsync(HttpContext context, CliServerOptions options, ILogger logger)
    {
        var remoteAddress = context.Connection.RemoteIpAddress?.ToString();

        if (!options.EnableShell)
        {
            var disabled = await context.WebSockets.AcceptWebSocketAsync();
            await ShellSessionManager.CloseWithAsync(disabled, CliShellCloseCodes.Disabled, CliShellAccess.ShellDisabled);
            return;
        }

        var bridge = context.RequestServices.GetService<ICliAuthBridge>();
        var access = await CliShellAccess.AuthorizeAsync(context, options.Shell, bridge != null, logger);
        var webSocket = await context.WebSockets.AcceptWebSocketAsync();

        if (!access.Allowed)
        {
            // Never log the presented token or the command.
            logger.LogInformation("Shell connection refused (auth={AuthMode}, remote={Remote}, code={Code}, reason={Reason})",
                access.Mode, remoteAddress, access.CloseCode, access.Reason);
            await ShellSessionManager.CloseWithAsync(webSocket, access.CloseCode, access.Reason);
            return;
        }

        var query = context.Request.Query;
        int.TryParse(query["cols"], out var cols);
        int.TryParse(query["rows"], out var rows);

        var shellManager = context.RequestServices.GetRequiredService<IShellSessionManager>();
        await shellManager.HandleShellSessionAsync(webSocket, new ShellSessionRequest
        {
            Cols = cols > 0 ? cols : 80,
            Rows = rows > 0 ? rows : 24,
            Command = query["cmd"].FirstOrDefault(),
            AuthMode = access.Mode,
            Principal = access.Principal,
            AuthBridge = access.Principal != null ? bridge : null,
            RemoteAddress = remoteAddress,
        }, context.RequestAborted);
    }

    /// <summary>Startup diagnostics: ERROR when the shell is locked, WARNING when anonymous.</summary>
    private static void LogShellConfiguration(IServiceProvider services)
    {
        var options = services.GetService<CliServerOptions>();
        if (options is not { EnableShell: true }) return;

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Qodalis.Cli.Shell");
        var mode = CliShellAccess.ResolveMode(options.Shell, services.GetService<ICliAuthBridge>() != null);
        switch (mode)
        {
            case CliShellAuthModes.Locked:
                logger.LogError(
                    "The shell is enabled but locked: no access mode is configured, every shell connection will be refused (4401). " +
                    "Install the users plugin (shell:execute permission), or configure cli.EnableShell(o => o.Token = \"...\"), " +
                    "o.Authorize = ctx => ..., or (unsafe) o.AllowAnonymous = true.");
                break;
            case CliShellAuthModes.Anonymous:
                logger.LogWarning("WARNING: shell is enabled without authentication (AllowAnonymous): anyone who can reach this server gets a shell.");
                break;
            default:
                logger.LogInformation("Shell enabled (auth={AuthMode})", mode);
                break;
        }
    }
}
