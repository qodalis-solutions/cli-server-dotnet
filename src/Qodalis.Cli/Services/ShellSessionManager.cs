using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Services;

/// <summary>
/// Manages interactive shell sessions over WebSocket, spawning a PTY process and bridging I/O.
/// Enforces <see cref="CliShellOptions"/>: concurrency limits (4429), idle timeout and maximum
/// duration (4408), session binding in users mode (4401 when the session ends or loses
/// <c>shell:execute</c>) and a minimal child environment.
/// </summary>
public class ShellSessionManager : IShellSessionManager
{
    /// <summary>Environment variables always passed from the server (when set).</summary>
    public static readonly IReadOnlyList<string> BaseEnvironment = ["PATH", "HOME", "USER", "LANG"];

    private static readonly string[] WindowsEnvironment =
        ["SystemRoot", "windir", "ComSpec", "PATHEXT", "TEMP", "TMP", "USERPROFILE"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger<ShellSessionManager> _logger;
    private readonly CliShellOptions _options;
    private readonly CliShellSessionRegistry _registry;

    /// <summary>
    /// Initializes a new instance of <see cref="ShellSessionManager"/>.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serverOptions">Server options (shell options are read from <see cref="CliServerOptions.Shell"/>).</param>
    /// <param name="registry">Registry of open shells (limits and session binding).</param>
    /// <param name="lifetime">When present, open shells are closed with 1001 on server shutdown.</param>
    public ShellSessionManager(ILogger<ShellSessionManager> logger, CliServerOptions? serverOptions = null,
        CliShellSessionRegistry? registry = null, IHostApplicationLifetime? lifetime = null)
    {
        _logger = logger;
        _options = serverOptions?.Shell ?? new CliShellOptions();
        _registry = registry ?? new CliShellSessionRegistry();
        lifetime?.ApplicationStopping.Register(() =>
            _registry.TerminateAll((int)WebSocketCloseStatus.EndpointUnavailable, ShellEndReasons.ServerShutdown));
    }

    /// <summary>Accepts nothing: closes an already accepted socket with a code and short reason.</summary>
    internal static async Task CloseWithAsync(WebSocket webSocket, int code, string reason)
    {
        if (webSocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await webSocket.CloseAsync((WebSocketCloseStatus)code, reason, cts.Token);
        }
        catch (Exception)
        {
            // client gone
        }
    }

    /// <summary>Mutable per-session state shared by the I/O loops and the watchdog.</summary>
    private sealed class SessionState
    {
        private long _lastStdinTicks = DateTimeOffset.UtcNow.UtcTicks;
        private int _stdinSinceCheck;
        private int _terminated;

        public DateTimeOffset LastStdin => new(Interlocked.Read(ref _lastStdinTicks), TimeSpan.Zero);
        public int CloseCode { get; private set; } = (int)WebSocketCloseStatus.NormalClosure;
        public string? EndReason { get; private set; }
        public string? AuditReason { get; private set; }
        public bool Failed { get; set; }
        public bool Terminated => Volatile.Read(ref _terminated) == 1;

        public void Stdin()
        {
            Interlocked.Exchange(ref _lastStdinTicks, DateTimeOffset.UtcNow.UtcTicks);
            Interlocked.Exchange(ref _stdinSinceCheck, 1);
        }

        public bool TakeStdinSinceCheck() => Interlocked.Exchange(ref _stdinSinceCheck, 0) == 1;

        public bool TryTerminate(int code, string reason, string? auditReason)
        {
            if (Interlocked.Exchange(ref _terminated, 1) == 1) return false;
            CloseCode = code;
            EndReason = reason;
            AuditReason = auditReason ?? reason;
            return true;
        }
    }

    /// <inheritdoc />
    public async Task HandleShellSessionAsync(
        WebSocket webSocket,
        ShellSessionRequest request,
        CancellationToken cancellationToken)
    {
        var bound = request.Principal != null && request.AuthBridge != null;
        var limitKey = request.Principal != null
            ? "user:" + request.Principal.UserId
            : "addr:" + (request.RemoteAddress ?? "unknown");
        var sessionMode = request.Command == null ? "interactive" : "command";

        var handle = _registry.TryRegister(limitKey, _options.MaxSessionsPerUser, _options.MaxSessions,
            request.AuthMode, request.Principal, request.RemoteAddress);
        if (handle == null)
        {
            _logger.LogWarning("Shell session refused: too many shell sessions (auth={AuthMode}, remote={Remote})",
                request.AuthMode, request.RemoteAddress);
            await CloseWithAsync(webSocket, CliShellCloseCodes.TooManySessions, "too many shell sessions");
            return;
        }

        var cols = request.Cols > 0 ? request.Cols : 80;
        var rows = request.Rows > 0 ? request.Rows : 24;
        var (shell, shellArgs) = GetShellInfo(request.Command);
        Process? process = null;
        var state = new SessionState();
        var started = Stopwatch.StartNew();
        var opened = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void Terminate(int code, string reason, string? auditReason)
        {
            if (!state.TryTerminate(code, reason, auditReason)) return;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            try
            {
                if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            }
            catch (Exception) { }
        }

        handle.OnTerminate = Terminate;

        try
        {
            // script -c takes one command line: quote every argument so a one-shot
            // command ("echo hi") reaches the shell as a single -c argument
            var shellCommand = shellArgs.Length > 0
                ? $"{EscapeShellArg(shell)} {string.Join(" ", shellArgs.Select(EscapeShellArg))}"
                : EscapeShellArg(shell);

            var psi = new ProcessStartInfo
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = GetWorkingDirectory(),
            };
            BuildEnvironment(psi.Environment, shell, cols, rows);

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

            process.Start();
            opened = true;

            // Never log or audit the command text.
            _logger.LogInformation(
                "Shell session opened (auth={AuthMode}, mode={Mode}, remote={Remote}, user={User}, shell={Shell})",
                request.AuthMode, sessionMode, request.RemoteAddress, request.Principal?.UserName, Path.GetFileName(shell));
            if (bound)
                await AuditAsync(request, new CliShellAuditEvent(CliShellAuditEvent.Opened, request.Principal!,
                    sessionMode, request.RemoteAddress));

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

            var outputTask = ReadProcessOutputAsync(process, webSocket, cts.Token);
            var inputTask = ReadWebSocketInputAsync(webSocket, process, state, cts.Token);
            var watchdogTask = WatchdogAsync(handle, request, state, started, Terminate, cts.Token);

            // Wait for the process to exit (don't send exit message yet)
            var processExitTask = process.WaitForExitAsync(cts.Token);

            // Stop when process exits, output ends, or WebSocket input closes
            await Task.WhenAny(outputTask, inputTask, processExitTask);

            // Output ended (EOF) usually means the process is exiting: give it a moment
            // so the client still gets the exit message (one-shot commands)
            if (outputTask.IsCompleted && !processExitTask.IsCompleted)
            {
                try
                {
                    await processExitTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch { }
            }

            // Process exited — drain remaining stdout before sending exit message
            if (processExitTask.IsCompletedSuccessfully && !state.Terminated)
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
                await Task.WhenAll(outputTask, inputTask, watchdogTask)
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shell session error");
            state.Failed = true;
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
            handle.OnTerminate = null;
            handle.Dispose();

            int? exitCode = null;
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                    else if (!state.Terminated)
                        exitCode = process.ExitCode;
                }
                catch (Exception killEx) { _logger.LogDebug(killEx, "Failed to kill shell process"); }
                process.Dispose();
            }

            if (opened)
            {
                var reason = state.AuditReason
                    ?? (state.Failed ? ShellEndReasons.Error : exitCode != null ? ShellEndReasons.Exited : ShellEndReasons.Closed);
                var durationMs = started.ElapsedMilliseconds;
                _logger.LogInformation(
                    "Shell session closed (auth={AuthMode}, mode={Mode}, remote={Remote}, user={User}, durationMs={DurationMs}, exitCode={ExitCode}, reason={Reason})",
                    request.AuthMode, sessionMode, request.RemoteAddress, request.Principal?.UserName, durationMs, exitCode, reason);
                if (bound)
                    await AuditAsync(request, new CliShellAuditEvent(CliShellAuditEvent.Closed, request.Principal!,
                        sessionMode, request.RemoteAddress, durationMs, exitCode, reason));
            }

            if (state.Terminated)
            {
                await CloseWithAsync(webSocket, state.CloseCode, state.EndReason ?? "closed");
            }
            else if (webSocket.State == WebSocketState.Open)
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

    private async Task AuditAsync(ShellSessionRequest request, CliShellAuditEvent auditEvent)
    {
        try
        {
            await request.AuthBridge!.AuditShellAsync(auditEvent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to audit {Event}", auditEvent.Event);
        }
    }

    /// <summary>
    /// Enforces the idle timeout and maximum duration, and in users mode re-validates the bound
    /// session (periodically, at most every <see cref="CliShellOptions.SessionCheckInterval"/>,
    /// and when the registry requests it).
    /// </summary>
    private async Task WatchdogAsync(CliShellSessionHandle handle, ShellSessionRequest request, SessionState state,
        Stopwatch started, Action<int, string, string?> terminate, CancellationToken cancellationToken)
    {
        var idle = _options.IdleTimeout is { } i && i > TimeSpan.Zero ? i : (TimeSpan?)null;
        var max = _options.MaxDuration is { } m && m > TimeSpan.Zero ? m : (TimeSpan?)null;
        var checkInterval = _options.EffectiveSessionCheckInterval;
        var bound = request.Principal != null && request.AuthBridge != null;

        var tick = TimeSpan.FromSeconds(1);
        if (idle != null) tick = Min(tick, idle.Value / 4);
        if (max != null) tick = Min(tick, max.Value / 4);
        if (bound) tick = Min(tick, checkInterval);
        if (tick < TimeSpan.FromMilliseconds(25)) tick = TimeSpan.FromMilliseconds(25);

        var principal = request.Principal;
        var lastCheck = Stopwatch.StartNew();
        Stopwatch? lastTouch = null; // stdin touches the session at most once per TouchInterval

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(tick, cancellationToken);

                if (idle != null && DateTimeOffset.UtcNow - state.LastStdin >= idle.Value)
                {
                    terminate(CliShellCloseCodes.Timeout, ShellEndReasons.TimedOut, null);
                    return;
                }

                if (max != null && started.Elapsed >= max.Value)
                {
                    terminate(CliShellCloseCodes.Timeout, ShellEndReasons.TimedOut, null);
                    return;
                }

                if (bound && (handle.TakeRecheck() || lastCheck.Elapsed >= checkInterval))
                {
                    lastCheck.Restart();
                    CliPrincipal? current;
                    try
                    {
                        var touch = (lastTouch == null || lastTouch.Elapsed >= TouchInterval) && state.TakeStdinSinceCheck();
                        if (touch) lastTouch = Stopwatch.StartNew();
                        current = await request.AuthBridge!.RevalidateAsync(principal!, touch);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Shell session re-validation failed; closing the shell");
                        current = null;
                    }

                    if (current == null)
                    {
                        terminate(CliShellCloseCodes.Unauthorized, ShellEndReasons.SessionEnded, null);
                        return;
                    }
                    if (!current.Can(CliRoutePolicy.ShellExecute))
                    {
                        terminate(CliShellCloseCodes.Unauthorized, ShellEndReasons.SessionEnded, ShellEndReasons.PermissionRemoved);
                        return;
                    }

                    principal = current;
                    handle.Principal = current;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Minimum time between two session touches caused by shell input.</summary>
    internal static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(30);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>
    /// Minimal child environment (C3): <c>PATH</c>, <c>HOME</c>, <c>USER</c>, <c>LANG</c> from the
    /// server, then <see cref="CliShellOptions.EnvironmentAllow"/>, then <c>TERM</c>,
    /// <c>COLUMNS</c>, <c>LINES</c>, <c>SHELL</c>, then <see cref="CliShellOptions.EnvironmentSet"/>.
    /// Server secrets are not inherited.
    /// </summary>
    private void BuildEnvironment(IDictionary<string, string?> env, string shell, int cols, int rows)
    {
        env.Clear();

        void CopyFromServer(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value != null) env[name] = value;
        }

        foreach (var name in BaseEnvironment) CopyFromServer(name);
        if (OperatingSystem.IsWindows())
            foreach (var name in WindowsEnvironment) CopyFromServer(name);

        if (!env.ContainsKey("HOME"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) env["HOME"] = home;
        }
        if (!env.ContainsKey("USER") && !string.IsNullOrEmpty(Environment.UserName))
            env["USER"] = Environment.UserName;

        foreach (var name in _options.EnvironmentAllow.Where(n => !string.IsNullOrWhiteSpace(n)))
            CopyFromServer(name.Trim());

        env["TERM"] = "xterm-256color";
        env["COLUMNS"] = cols.ToString();
        env["LINES"] = rows.ToString();
        env["SHELL"] = shell;

        foreach (var (name, value) in _options.EnvironmentSet)
            env[name] = value;
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

    private static async Task ReadWebSocketInputAsync(
        WebSocket webSocket,
        Process process,
        SessionState state,
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
                        state.Stdin();
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

    private (string shell, string[] args) GetShellInfo(string? command)
    {
        if (!string.IsNullOrWhiteSpace(_options.Shell))
        {
            return command != null
                ? (_options.Shell, OperatingSystem.IsWindows() ? new[] { "-Command", command } : new[] { "-c", command })
                : (_options.Shell, Array.Empty<string>());
        }

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

    private string GetWorkingDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.WorkingDirectory))
            return _options.WorkingDirectory;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && Directory.Exists(home) ? home : "/";
    }

    private static string EscapeShellArg(string arg)
    {
        return "'" + arg.Replace("'", "'\\''") + "'";
    }
}

/// <summary>
/// Canonical <c>shell_closed</c> audit reasons (web-cli spec C7). The close reason sent to the
/// client is <c>session ended</c> for both <see cref="SessionEnded"/> and <see cref="PermissionRemoved"/>.
/// </summary>
public static class ShellEndReasons
{
    public const string Exited = "exited";
    public const string Closed = "closed";
    public const string SessionEnded = "session ended";
    public const string PermissionRemoved = "permission removed";
    public const string TimedOut = "shell timed out";
    public const string ServerShutdown = "server shutdown";
    public const string Error = "error";
}
