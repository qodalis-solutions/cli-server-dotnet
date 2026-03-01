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
            if (eventsPath == "/ws/cli/events" || eventsPath == "/ws/v1/cli/events" || eventsPath == "/ws/v2/cli/events")
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

            var terminalPath = context.Request.Path.Value;
            if (terminalPath == "/ws/cli" || terminalPath == "/ws/v1/cli" || terminalPath == "/ws/v2/cli")
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
