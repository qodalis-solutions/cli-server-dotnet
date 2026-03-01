using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Pty.Net;

namespace Qodalis.Cli.Services;

public class ShellSessionManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task HandleShellSessionAsync(
        WebSocket webSocket,
        int cols,
        int rows,
        string? command,
        CancellationToken cancellationToken)
    {
        var (shell, shellArgs) = GetShellInfo(command);
        IPtyConnection? pty = null;

        try
        {
            var options = new PtyOptions
            {
                Name = "qodalis-shell",
                Cols = cols,
                Rows = rows,
                Cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                App = shell,
                CommandLine = shellArgs,
                Environment = GetEnvironment(),
            };

            pty = await PtyProvider.SpawnAsync(options, cancellationToken);

            var os = Environment.OSVersion.Platform switch
            {
                PlatformID.Win32NT => "win32",
                PlatformID.Unix => OperatingSystem.IsMacOS() ? "darwin" : "linux",
                _ => "unknown",
            };

            await SendJsonAsync(webSocket, new
            {
                type = "ready",
                shell = Path.GetFileName(shell),
                os,
            }, cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var outputTask = ReadPtyOutputAsync(pty, webSocket, cts.Token);
            var inputTask = ReadWebSocketInputAsync(webSocket, pty, cts.Token);
            var exitTask = WaitForExitAsync(pty, webSocket, cts);

            await Task.WhenAny(outputTask, inputTask, exitTask);
            cts.Cancel();

            try
            {
                await Task.WhenAll(outputTask, inputTask, exitTask)
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }
        catch (Exception ex)
        {
            await SendJsonAsync(webSocket, new
            {
                type = "error",
                message = ex.Message,
            }, cancellationToken);
        }
        finally
        {
            pty?.Kill();
            pty?.Dispose();

            if (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Shell session ended",
                        CancellationToken.None);
                }
                catch { }
            }
        }
    }

    private async Task ReadPtyOutputAsync(
        IPtyConnection pty,
        WebSocket webSocket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   webSocket.State == WebSocketState.Open)
            {
                var bytesRead = await pty.ReaderStream.ReadAsync(
                    buffer, cancellationToken);
                if (bytesRead == 0) break;

                var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                await SendJsonAsync(webSocket, new
                {
                    type = "stdout",
                    data,
                }, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private async Task ReadWebSocketInputAsync(
        WebSocket webSocket,
        IPtyConnection pty,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using var doc = JsonDocument.Parse(json);
                var msgType = doc.RootElement.GetProperty("type").GetString();

                switch (msgType)
                {
                    case "stdin":
                        var data = doc.RootElement.GetProperty("data").GetString();
                        if (data != null)
                        {
                            var bytes = Encoding.UTF8.GetBytes(data);
                            await pty.WriterStream.WriteAsync(bytes, cancellationToken);
                            await pty.WriterStream.FlushAsync(cancellationToken);
                        }
                        break;

                    case "resize":
                        var newCols = doc.RootElement.GetProperty("cols").GetInt32();
                        var newRows = doc.RootElement.GetProperty("rows").GetInt32();
                        pty.Resize(newCols, newRows);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private async Task WaitForExitAsync(
        IPtyConnection pty,
        WebSocket webSocket,
        CancellationTokenSource cts)
    {
        try
        {
            // sch.pty.net WaitForExit takes int timeout (ms), not CancellationToken.
            // Poll with short intervals to respect cancellation.
            await Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    if (pty.WaitForExit(500))
                        break;
                }
            }, cts.Token);

            if (!cts.Token.IsCancellationRequested && webSocket.State == WebSocketState.Open)
            {
                await SendJsonAsync(webSocket, new
                {
                    type = "exit",
                    code = pty.ExitCode,
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }

        cts.Cancel();
    }

    private static async Task SendJsonAsync(
        WebSocket webSocket,
        object message,
        CancellationToken cancellationToken)
    {
        if (webSocket.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(message, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }

    private static (string shell, string[] args) GetShellInfo(string? command)
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            var shell = "powershell.exe";
            return command != null
                ? (shell, new[] { "-Command", command })
                : (shell, Array.Empty<string>());
        }
        else
        {
            var shell = "/bin/bash";
            return command != null
                ? (shell, new[] { "-c", command })
                : (shell, Array.Empty<string>());
        }
    }

    private static Dictionary<string, string> GetEnvironment()
    {
        var env = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                env[key] = value;
            }
        }

        env["TERM"] = "xterm-256color";
        return env;
    }
}
