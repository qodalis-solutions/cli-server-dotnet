using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Services;
using System.Diagnostics;
using System.Text;

namespace Qodalis.Cli.Extensions;

public static class WebApplicationExtensions
{
    public static IApplicationBuilder UseCli(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            var eventsPath = context.Request.Path.Value;
            if (eventsPath == "/ws/v1/qcli/events" || eventsPath == "/ws/qcli/events")
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    var manager = context.RequestServices.GetRequiredService<CliEventSocketManager>();
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
                    var shellManager = context.RequestServices.GetRequiredService<ShellSessionManager>();
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
                    var logManager = context.RequestServices.GetRequiredService<CliLogSocketManager>();
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
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    Console.WriteLine("WebSocket connected");

                    var bashProcess = StartBashProcess();
                    await HandleWebSocketCommunication(webSocket, bashProcess);
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

    private static Process StartBashProcess()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "bash",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            process.StandardInput.WriteLine("stty raw -echo");
        }

        process.StandardInput.WriteLine("stty raw -echo");

        return process;
    }

    private static async Task HandleWebSocketCommunication(System.Net.WebSockets.WebSocket webSocket, Process bashProcess)
    {
        var buffer = new byte[1024 * 4];

        var outputTask = Task.Run(async () =>
        {
            while (!bashProcess.HasExited)
            {
                var output = new char[1024];
                var count = await bashProcess.StandardOutput.ReadAsync(output, 0, output.Length);
                if (count > 0)
                {
                    var data = new ArraySegment<byte>(Encoding.UTF8.GetBytes(output, 0, count));
                    await webSocket.SendAsync(data, System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
        });

        while (webSocket.State == System.Net.WebSockets.WebSocketState.Open)
        {
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                break;
            }

            var input = Encoding.UTF8.GetString(buffer, 0, result.Count);
            await bashProcess.StandardInput.WriteAsync(input);
        }

        await outputTask;
        bashProcess.Kill();
    }
}
