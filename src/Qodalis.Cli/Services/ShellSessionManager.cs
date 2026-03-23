using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Qodalis.Cli.Services;

/// <summary>
/// Manages interactive shell sessions over WebSocket, spawning a PTY process and bridging I/O.
/// </summary>
public class ShellSessionManager : IShellSessionManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger<ShellSessionManager> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ShellSessionManager"/>.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public ShellSessionManager(ILogger<ShellSessionManager> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
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

            var psi = new ProcessStartInfo
            {
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
            };

            if (OperatingSystem.IsMacOS())
            {
                // macOS: wrap script in bash with SIGINT ignored.
                // trap '' INT makes the shell ignore SIGINT; exec preserves
                // ignored signal dispositions, so script won't die on Ctrl+C.
                // The child shell (zsh) resets its own handlers via the PTY.
                var scriptArgs = $"script -q /dev/null {EscapeShellArg(shell)}";
                foreach (var arg in shellArgs)
                    scriptArgs += $" {EscapeShellArg(arg)}";

                psi.FileName = "bash";
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add($"trap '' INT; exec {scriptArgs}");
            }
            else
            {
                // Linux: script -q -c "command" /dev/null
                psi.FileName = "script";
                psi.ArgumentList.Add("-q");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(shellCommand);
                psi.ArgumentList.Add("/dev/null");
            }

            process = new Process
            {
                StartInfo = psi,
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

            _logger.LogInformation("Shell session started (shell={Shell}, cols={Cols}, rows={Rows})", Path.GetFileName(shell), cols, rows);

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

            // Wait for the process to exit (don't send exit message yet)
            var processExitTask = process.WaitForExitAsync(cts.Token);

            // Stop when process exits, output ends, or WebSocket input closes
            await Task.WhenAny(outputTask, inputTask, processExitTask);

            // Process exited — drain remaining stdout before sending exit message
            if (processExitTask.IsCompleted)
            {
                try
                {
                    await outputTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch { }

                if (webSocket.State == WebSocketState.Open)
                {
                    await SendJsonAsync(webSocket, new
                    {
                        type = "exit",
                        code = process.ExitCode,
                    }, CancellationToken.None);
                }
            }

            cts.Cancel();

            try
            {
                await Task.WhenAll(outputTask, inputTask)
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shell session error");
            try
            {
                await SendJsonAsync(webSocket, new
                {
                    type = "error",
                    message = ex.Message,
                }, CancellationToken.None);
            }
            catch (Exception sendEx) { _logger.LogDebug(sendEx, "Failed to send error message to client"); }
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
                catch (Exception killEx) { _logger.LogDebug(killEx, "Failed to kill shell process"); }
                process.Dispose();

                _logger.LogInformation("Shell session ended");
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
                catch (Exception closeEx) { _logger.LogDebug(closeEx, "Failed to close WebSocket gracefully"); }
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
                   webSocket.State == WebSocketState.Open)
            {
                var charsRead = await process.StandardOutput.ReadAsync(
                    buffer, cancellationToken);
                if (charsRead == 0) break; // EOF — process closed stdout

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
                   webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (process.HasExited)
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
                            try
                            {
                                await process.StandardInput.WriteAsync(data);
                                await process.StandardInput.FlushAsync();
                            }
                            catch (IOException) { return; }
                        }
                        break;

                    case "resize":
                        var newCols = doc.RootElement.GetProperty("cols").GetInt32();
                        var newRows = doc.RootElement.GetProperty("rows").GetInt32();
                        try
                        {
                            await process.StandardInput.WriteAsync(
                                $"stty cols {newCols} rows {newRows}\n");
                            await process.StandardInput.FlushAsync();
                        }
                        catch (Exception) { }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (IOException) { }
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

    private static string EscapeShellArg(string arg)
    {
        return "'" + arg.Replace("'", "'\\''") + "'";
    }
}
