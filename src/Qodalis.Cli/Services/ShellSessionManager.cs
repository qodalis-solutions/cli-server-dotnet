using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

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
        Process? process = null;

        try
        {
            var shellCommand = shellArgs.Length > 0
                ? $"{shell} {string.Join(" ", shellArgs)}"
                : shell;

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "script",
                    ArgumentList =
                    {
                        "-q",       // quiet
                        "-c",       // command
                        shellCommand,
                        "/dev/null" // output file (discard)
                    },
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = GetWorkingDirectory(),
                    Environment =
                    {
                        ["TERM"] = "xterm-256color",
                        ["COLUMNS"] = cols.ToString(),
                        ["LINES"] = rows.ToString(),
                    },
                },
                EnableRaisingEvents = true,
            };

            // Copy existing environment variables
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string key && entry.Value is string value
                    && !process.StartInfo.Environment.ContainsKey(key))
                {
                    process.StartInfo.Environment[key] = value;
                }
            }

            process.Start();

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
            var outputTask = ReadProcessOutputAsync(process, webSocket, cts.Token);
            var inputTask = ReadWebSocketInputAsync(webSocket, process, cts.Token);
            var exitTask = WaitForExitAsync(process, webSocket, cts);

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
            Console.Error.WriteLine($"Shell session error: {ex}");
            try
            {
                await SendJsonAsync(webSocket, new
                {
                    type = "error",
                    message = ex.Message,
                }, CancellationToken.None);
            }
            catch { }
        }
        finally
        {
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
                process.Dispose();
            }

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

    private async Task ReadProcessOutputAsync(
        Process process,
        WebSocket webSocket,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   webSocket.State == WebSocketState.Open &&
                   !process.HasExited)
            {
                var charsRead = await process.StandardOutput.ReadAsync(
                    buffer, cancellationToken);
                if (charsRead == 0) break;

                var data = new string(buffer, 0, charsRead);
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
        Process process,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   webSocket.State == WebSocketState.Open &&
                   !process.HasExited)
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
                            await process.StandardInput.WriteAsync(data);
                            await process.StandardInput.FlushAsync();
                        }
                        break;

                    case "resize":
                        // script doesn't support runtime resize, but we can try stty
                        var newCols = doc.RootElement.GetProperty("cols").GetInt32();
                        var newRows = doc.RootElement.GetProperty("rows").GetInt32();
                        try
                        {
                            await process.StandardInput.WriteAsync(
                                $"stty cols {newCols} rows {newRows}\n");
                            await process.StandardInput.FlushAsync();
                        }
                        catch { }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private async Task WaitForExitAsync(
        Process process,
        WebSocket webSocket,
        CancellationTokenSource cts)
    {
        try
        {
            await process.WaitForExitAsync(cts.Token);

            if (!cts.Token.IsCancellationRequested && webSocket.State == WebSocketState.Open)
            {
                await SendJsonAsync(webSocket, new
                {
                    type = "exit",
                    code = process.ExitCode,
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
            var shell = DetectShell();
            return command != null
                ? (shell, new[] { "-c", command })
                : (shell, Array.Empty<string>());
        }
    }

    private static string DetectShell()
    {
        var envShell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrEmpty(envShell) && File.Exists(envShell))
            return envShell;

        string[] candidates = ["/bin/bash", "/usr/bin/bash", "/bin/sh"];
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "/bin/sh";
    }

    private static string GetWorkingDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && Directory.Exists(home) ? home : "/";
    }
}
