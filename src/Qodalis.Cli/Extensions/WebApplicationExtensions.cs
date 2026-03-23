using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Extensions;

/// <summary>
/// Extension methods for <see cref="IApplicationBuilder"/> to configure CLI WebSocket middleware.
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>
    /// Adds CLI middleware for WebSocket endpoints (events, shell, logs, and terminal).
    /// Must be called after <c>UseWebSockets()</c>.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseCli(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            var eventsPath = context.Request.Path.Value;
            if (eventsPath == "/ws/v1/qcli/events" || eventsPath == "/ws/qcli/events")
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    var manager = context.RequestServices.GetRequiredService<ICliEventSocketManager>();
                    var socket = await context.WebSockets.AcceptWebSocketAsync();
                    await manager.HandleConnectionAsync(socket, context.RequestAborted);
                }
                else
                {
                    context.Response.StatusCode = 400;
                }

                return;
            }

            var shellPath = context.Request.Path.Value;
            if (shellPath == "/ws/v1/qcli/shell" || shellPath == "/ws/qcli/shell")
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    var shellManager = context.RequestServices.GetRequiredService<IShellSessionManager>();
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();

                    var query = context.Request.Query;
                    int.TryParse(query["cols"], out var cols);
                    int.TryParse(query["rows"], out var rows);
                    if (cols <= 0) cols = 80;
                    if (rows <= 0) rows = 24;
                    var cmd = query["cmd"].FirstOrDefault();

                    await shellManager.HandleShellSessionAsync(
                        webSocket, cols, rows, cmd, context.RequestAborted);
                }
                else
                {
                    context.Response.StatusCode = 400;
                }

                return;
            }

            var logsPath = context.Request.Path.Value;
            if (logsPath == "/ws/v1/qcli/logs" || logsPath == "/ws/qcli/logs")
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    var logManager = context.RequestServices.GetRequiredService<ICliLogSocketManager>();
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    var levelFilter = context.Request.Query["level"].FirstOrDefault();
                    await logManager.HandleConnectionAsync(webSocket, levelFilter, context.RequestAborted);
                }
                else
                {
                    context.Response.StatusCode = 400;
                }

                return;
            }

            var terminalPath = context.Request.Path.Value;
            if (terminalPath == "/ws/v1/qcli" || terminalPath == "/ws/qcli")
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    var terminalManager = context.RequestServices.GetRequiredService<IShellSessionManager>();
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    await terminalManager.HandleShellSessionAsync(
                        webSocket, 80, 24, null, context.RequestAborted);
                }
                else
                {
                    context.Response.StatusCode = 400;
                }

                return;
            }

            await next();
        });

        return app;
    }
}
