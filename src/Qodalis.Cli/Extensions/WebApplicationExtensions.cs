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

            if ((kind == "shell" || kind == "terminal") && !options.EnableShell)
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
                case "shell":
                {
                    var shellManager = context.RequestServices.GetRequiredService<IShellSessionManager>();
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    var query = context.Request.Query;
                    int.TryParse(query["cols"], out var cols);
                    int.TryParse(query["rows"], out var rows);
                    if (cols <= 0) cols = 80;
                    if (rows <= 0) rows = 24;
                    var cmd = query["cmd"].FirstOrDefault();

                    // Never log the command itself: it may contain secrets.
                    logger.LogDebug("Shell WebSocket connection accepted (cols={Cols}, rows={Rows}, mode={Mode})",
                        cols, rows, cmd == null ? "interactive" : "command");
                    await shellManager.HandleShellSessionAsync(webSocket, cols, rows, cmd, context.RequestAborted);
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
                default:
                {
                    logger.LogDebug("Terminal WebSocket connection accepted");
                    var terminalManager = context.RequestServices.GetRequiredService<IShellSessionManager>();
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    await terminalManager.HandleShellSessionAsync(webSocket, 80, 24, null, context.RequestAborted);
                    break;
                }
            }
        });

        return app;
    }
}
