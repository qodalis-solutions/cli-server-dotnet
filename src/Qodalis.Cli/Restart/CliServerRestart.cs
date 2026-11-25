using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Abstractions.Restart;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Restart;

/// <summary>What the <c>handler</c> mode's function receives.</summary>
/// <param name="Reason">Why (trimmed), or null.</param>
/// <param name="Pending">The ids of the changes that were pending.</param>
/// <param name="By">Who asked (<c>anonymous</c> without sign-in).</param>
public sealed record CliServerRestartContext(string? Reason, IReadOnlyList<string> Pending, string By);

/// <summary>The command a <c>self</c> restart starts: the same executable, arguments and working directory.</summary>
public sealed record CliServerRestartCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

/// <summary>
/// What the process does, injectable so tests never stop the host or start processes: stopping the host (with an exit
/// code), starting the same command again, and reading the environment and files the supervisor detection looks at.
/// </summary>
public interface ICliServerRestartRuntime
{
    /// <summary>The process's environment variables.</summary>
    IReadOnlyDictionary<string, string> EnvironmentVariables { get; }

    bool FileExists(string path);

    /// <summary>The file's text, or null when it cannot be read.</summary>
    string? ReadFile(string path);

    /// <summary>The executable of this process (<see cref="Environment.ProcessPath"/>).</summary>
    string? ProcessPath { get; }

    /// <summary>The process name (<c>w3wp</c> and <c>iisexpress</c> are IIS in-process hosts).</summary>
    string ProcessName { get; }

    /// <summary><see cref="Environment.GetCommandLineArgs"/>: the program first, then its arguments.</summary>
    IReadOnlyList<string> CommandLineArgs { get; }

    /// <summary>The entry assembly's file (<c>app.dll</c>), what <c>dotnet</c> runs.</summary>
    string? EntryAssemblyPath { get; }

    string CurrentDirectory { get; }

    /// <summary>
    /// Stops the host gracefully (<see cref="IHostApplicationLifetime.StopApplication"/>: in-flight requests finish
    /// within <paramref name="shutdownTimeout"/>, hosted services stop, the container disposes the singletons) and sets
    /// the process's exit code; <paramref name="afterStopped"/> runs once the host stopped (the port is free).
    /// </summary>
    void StopHost(int exitCode, TimeSpan shutdownTimeout, Func<Task>? afterStopped = null);

    /// <summary>Sets the process's exit code (<see cref="Environment.ExitCode"/>).</summary>
    void SetExitCode(int exitCode);

    /// <summary>Starts a process with this process's environment, its output where this process writes; does not wait.</summary>
    void Start(CliServerRestartCommand command);
}

/// <summary>The real process: <see cref="IHostApplicationLifetime"/>, <see cref="Environment"/>, <see cref="Process"/>.</summary>
public sealed class CliServerProcessRuntime(IHostApplicationLifetime? lifetime, IOptions<HostOptions>? hostOptions = null,
    ILogger? logger = null) : ICliServerRestartRuntime
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public IReadOnlyDictionary<string, string> EnvironmentVariables =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => e.Value?.ToString() ?? string.Empty, StringComparer.Ordinal);

    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public string? ProcessPath => Environment.ProcessPath;

    public string ProcessName
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            return process.ProcessName;
        }
    }

    public IReadOnlyList<string> CommandLineArgs => Environment.GetCommandLineArgs();

    public string? EntryAssemblyPath => Assembly.GetEntryAssembly()?.Location is { Length: > 0 } location ? location : null;

    public string CurrentDirectory => Directory.GetCurrentDirectory();

    public void StopHost(int exitCode, TimeSpan shutdownTimeout, Func<Task>? afterStopped = null)
    {
        if (lifetime == null) throw new InvalidOperationException("no host to stop: IHostApplicationLifetime is not registered");
        // The host reads its shutdown timeout when it stops: in-flight requests get this long
        if (hostOptions?.Value is { } options) options.ShutdownTimeout = shutdownTimeout;
        Environment.ExitCode = exitCode;
        if (afterStopped != null)
        {
            lifetime.ApplicationStopped.Register(() =>
            {
                try
                {
                    afterStopped().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "The restart's last step failed");
                }
            });
        }
        lifetime.StopApplication();
    }

    public void SetExitCode(int exitCode) => Environment.ExitCode = exitCode;

    public void Start(CliServerRestartCommand command)
    {
        var info = new ProcessStartInfo(command.FileName)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {command.FileName}");
        _logger.LogInformation("Started the server again (pid {Pid}); this process exits", process.Id);
    }
}

/// <summary>Options of restarting the server (<c>cli.ConfigureRestart(o =&gt; ...)</c>).</summary>
public sealed class CliServerRestartOptions
{
    /// <summary>The exit code of <c>exit</c> mode unless set (EX_TEMPFAIL: "try again").</summary>
    public const int DefaultExitCode = 75;

    /// <summary><see cref="CliServerRestartModes"/>; default <c>auto</c>: <c>exit</c> under a detected supervisor, else not supported.</summary>
    public string Mode { get; set; } = CliServerRestartModes.Auto;

    /// <summary>The exit code of <c>exit</c> mode (default 75).</summary>
    public int ExitCode { get; set; } = DefaultExitCode;

    /// <summary><c>handler</c> mode: runs once the host stopped; it restarts (or exits) the process.</summary>
    public Func<CliServerRestartContext, Task>? Handler { get; set; }

    /// <summary>
    /// Allow restarting on a server without sign-in (no auth provider protecting it). Default false: the API and the
    /// command refuse with 403, since anyone could restart it.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>How long the graceful shutdown waits for in-flight requests (the host's shutdown timeout; default 10 s).</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Replaces what the process does (tests); default the real process (<see cref="CliServerProcessRuntime"/>).</summary>
    public ICliServerRestartRuntime? Runtime { get; set; }

    /// <summary>Throws <see cref="ArgumentException"/> for an unknown mode, <c>handler</c> without a handler, or a bad timeout.</summary>
    public void Validate()
    {
        if (!CliServerRestartModes.All.Contains(Mode)) throw new ArgumentException($"invalid restart mode: {Mode}", nameof(Mode));
        if (Mode == CliServerRestartModes.Handler && Handler == null)
            throw new ArgumentException("restart mode 'handler' needs a Handler", nameof(Handler));
        if (ShutdownTimeout <= TimeSpan.Zero) throw new ArgumentException("invalid restart shutdown timeout", nameof(ShutdownTimeout));
    }
}

/// <summary>
/// The server's restart service: the changes waiting for a restart (<see cref="MarkRequired"/>,
/// <see cref="ClearRequired"/>; empty on every start) and the restart itself: audit <c>server.restart</c>, event
/// <c>server:restarting</c>, then a graceful shutdown (every WebSocket closed with 1012, the shutdown hooks, then the host
/// stopped: in-flight requests finish within the shutdown timeout, hosted services such as the job scheduler stop, the
/// container disposes the plugins' services) and the mode's action.
/// </summary>
public sealed partial class CliServerRestart : ICliServerRestart
{
    /// <summary>Why restarting is refused when no mode applies.</summary>
    public const string NotSupported = "restart not supported: no supervisor detected; set restart.mode";

    /// <summary>The longest reason kept.</summary>
    public const int MaxReasonLength = 500;

    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SocketCloseTimeout = TimeSpan.FromSeconds(5);

    private readonly CliServerRestartOptions _options;
    private readonly ICliServerRestartRuntime _runtime;
    private readonly IServiceProvider? _services;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly List<CliRestartRequirement> _items = [];
    private readonly List<(string Name, Func<Task> Run)> _hooks = [];
    private bool _restarting;

    public CliServerRestart(CliServerRestartOptions options, ICliServerRestartRuntime runtime, IServiceProvider? services = null,
        ILogger? logger = null, TimeProvider? time = null)
    {
        options.Validate();
        _options = options;
        _runtime = runtime;
        _services = services;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        StartedAt = Now();
    }

    /// <summary>The event bus the restart events go to; default the server's.</summary>
    public Func<ICliEventBus?>? EventBus { get; set; }

    /// <summary>Where restarts are audited; default the server's <see cref="IAuditStore"/>.</summary>
    public Func<IAuditStore?>? AuditStore { get; set; }

    /// <summary>True when an auth provider protects the server (callers are signed in); default <see cref="CliServerOptions.RequireAuthentication"/>.</summary>
    public Func<bool>? IsProtected { get; set; }

    private ICliEventBus? Bus => EventBus != null ? EventBus() : _services?.GetService<ICliEventBus>();
    private IAuditStore? Audit => AuditStore != null ? AuditStore() : _services?.GetService<IAuditStore>();
    private bool Protected => IsProtected != null ? IsProtected() : _services?.GetService<CliServerOptions>()?.RequireAuthentication == true;

    public string StartedAt { get; }

    public bool Restarting
    {
        get
        {
            lock (_lock) return _restarting;
        }
    }

    /// <summary>The graceful shutdown and the mode's action of an accepted restart (tests await it); null before one.</summary>
    public Task? Completion { get; private set; }

    private string Now() => _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Runs <paramref name="run"/> during the graceful shutdown, after the sockets closed, in registration order (errors are logged).</summary>
    public void OnShutdown(string name, Func<Task> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_lock) _hooks.Add((name, run));
    }

    // ---------------------------------------------------------------- supervisor and mode

    [GeneratedRegex("kubepods")]
    private static partial Regex KubePods();

    [GeneratedRegex("docker")]
    private static partial Regex Docker();

    /// <summary>
    /// The supervisor this process runs under, from its environment and files: PM2 (<c>pm_id</c>), systemd
    /// (<c>INVOCATION_ID</c>), IIS (an <c>ASPNETCORE_IIS_*</c> variable, or the in-process host <c>w3wp</c> /
    /// <c>iisexpress</c>), Kubernetes (<c>KUBERNETES_SERVICE_HOST</c>, or <c>kubepods</c> in <c>/proc/1/cgroup</c>), Docker
    /// (<c>/.dockerenv</c>, or <c>docker</c> in <c>/proc/1/cgroup</c>); null for none.
    /// </summary>
    public static string? DetectSupervisor(ICliServerRestartRuntime runtime)
    {
        var env = runtime.EnvironmentVariables;
        if (env.ContainsKey("pm_id")) return CliServerSupervisors.Pm2;
        if (env.TryGetValue("INVOCATION_ID", out var invocation) && invocation.Length > 0) return CliServerSupervisors.Systemd;
        if (env.Keys.Any(k => k.StartsWith("ASPNETCORE_IIS_", StringComparison.OrdinalIgnoreCase))
            || runtime.ProcessName.Equals("w3wp", StringComparison.OrdinalIgnoreCase)
            || runtime.ProcessName.Equals("iisexpress", StringComparison.OrdinalIgnoreCase))
            return CliServerSupervisors.Iis;
        var cgroup = runtime.ReadFile("/proc/1/cgroup") ?? string.Empty;
        if ((env.TryGetValue("KUBERNETES_SERVICE_HOST", out var k8s) && k8s.Length > 0) || KubePods().IsMatch(cgroup))
            return CliServerSupervisors.Kubernetes;
        if (runtime.FileExists("/.dockerenv") || Docker().IsMatch(cgroup)) return CliServerSupervisors.Docker;
        return null;
    }

    /// <summary>The mode in effect (null when not supported) and the supervisor detected.</summary>
    private (string? Mode, string? Supervisor) ResolveMode()
    {
        var supervisor = DetectSupervisor(_runtime);
        return _options.Mode == CliServerRestartModes.Auto
            ? (supervisor != null ? CliServerRestartModes.Exit : null, supervisor)
            : (_options.Mode, supervisor);
    }

    public CliServerRestartStatus Status()
    {
        var (mode, supervisor) = ResolveMode();
        return new CliServerRestartStatus
        {
            Supported = mode != null,
            Mode = mode,
            Supervisor = supervisor,
            Reason = mode == null ? NotSupported["restart not supported: ".Length..] : null,
            StartedAt = StartedAt,
            Pending = Pending(),
        };
    }

    // ---------------------------------------------------------------- pending changes

    public IReadOnlyList<CliRestartRequirement> Pending()
    {
        lock (_lock)
            return _items.Select(i => new CliRestartRequirement { Id = i.Id, Source = i.Source, Description = i.Description, Since = i.Since }).ToList();
    }

    public void MarkRequired(CliRestartRequirementInput requirement)
    {
        if (requirement == null || string.IsNullOrEmpty(requirement.Id) || requirement.Source == null || requirement.Description == null)
            throw new ArgumentException("MarkRequired needs an id, a source and a description");
        lock (_lock)
        {
            if (_items.Any(i => i.Id == requirement.Id)) return;
            _items.Add(new CliRestartRequirement
            {
                Id = requirement.Id,
                Source = requirement.Source,
                Description = requirement.Description,
                Since = Now(),
            });
        }
        _logger.LogInformation("Restart required: {Description}", requirement.Description);
        PublishPending();
    }

    public bool ClearRequired(string id)
    {
        lock (_lock)
        {
            if (_items.RemoveAll(i => i.Id == id) == 0) return false;
        }
        PublishPending();
        return true;
    }

    private void PublishPending() =>
        Publish(CliServerRestartConstants.RequiredEvent, new Dictionary<string, object?> { ["pending"] = Pending() });

    private void Publish(string type, Dictionary<string, object?> data)
    {
        var bus = Bus;
        if (bus == null) return;
        try
        {
            bus.PublishAsync(type, data, CliEventAudience.WithPermission(CliRoutePolicy.AdminAccess))
                .ContinueWith(t => _logger.LogError(t.Exception?.GetBaseException(), "Publishing {Type} failed", type),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Publishing {Type} failed", type);
        }
    }

    // ---------------------------------------------------------------- restart

    public async Task<CliServerRestartResult> RestartAsync(CliServerRestartRequest? request = null, CancellationToken cancellationToken = default)
    {
        request ??= new CliServerRestartRequest();
        if (request.Reason is { Length: > MaxReasonLength })
            throw new CliServerRestartException(400, $"reason must be at most {MaxReasonLength} characters");
        var (mode, supervisor) = ResolveMode();
        if (mode == null) throw new CliServerRestartException(409, NotSupported);
        if (request.Actor == null && !Protected && !_options.AllowAnonymous)
            throw new CliServerRestartException(403, "restart needs sign-in: protect the server (users plugin) or set restart.allowAnonymous");
        List<string> pending;
        lock (_lock)
        {
            if (_restarting) throw new CliServerRestartException(409, "restart already in progress");
            _restarting = true;
            pending = _items.Select(i => i.Id).ToList();
        }

        var by = request.Actor?.UserName ?? "anonymous";
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        _logger.LogInformation("Restarting the server ({Mode} mode), asked by {By}{Reason}", mode, by, reason != null ? $": {reason}" : "");
        await AuditAsync(reason, by, pending, mode, supervisor, request);
        var data = new Dictionary<string, object?>();
        if (reason != null) data["reason"] = reason;
        data["by"] = by;
        data["pending"] = pending;
        data["mode"] = mode;
        if (Bus is { } bus)
        {
            try
            {
                await bus.PublishAsync(CliServerRestartConstants.RestartingEvent, data, CliEventAudience.WithPermission(CliRoutePolicy.AdminAccess),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Publishing {Type} failed", CliServerRestartConstants.RestartingEvent);
            }
        }
        // In the background, after the caller answered (its response is an in-flight request the host waits for)
        var context = new CliServerRestartContext(reason, pending, by);
        Completion = Task.Run(async () =>
        {
            await Task.Yield();
            try
            {
                await RunAsync(mode, context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Restart failed");
            }
        });
        return new CliServerRestartResult { Restarting = true, Pending = pending };
    }

    private async Task AuditAsync(string? reason, string by, IReadOnlyList<string> pending, string mode, string? supervisor,
        CliServerRestartRequest request)
    {
        if (Audit is not { } audit) return;
        var properties = new Dictionary<string, object?>();
        if (reason != null) properties["reason"] = reason;
        properties["by"] = by;
        properties["pending"] = pending.ToList();
        properties["mode"] = mode;
        if (supervisor != null) properties["supervisor"] = supervisor;
        if (request.Via != null) properties["via"] = request.Via;
        try
        {
            await audit.LogAsync(new AuditEntry
            {
                Timestamp = _time.GetUtcNow().ToUnixTimeMilliseconds(),
                Event = CliServerRestartConstants.AuditEvent,
                Username = by,
                UserId = request.Actor?.UserId,
                ActorId = request.Actor?.UserId,
                Actor = by,
                Target = "server",
                Details = reason ?? (pending.Count > 0 ? $"pending: {string.Join(", ", pending)}" : "restart"),
                Properties = properties,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auditing the restart failed");
        }
    }

    /// <summary>The graceful shutdown, then the mode's action.</summary>
    private async Task RunAsync(string mode, CliServerRestartContext context)
    {
        await ShutdownAsync();
        switch (mode)
        {
            case CliServerRestartModes.Exit:
                _logger.LogInformation("Stopping with exit code {Code} for the supervisor to start the server again", _options.ExitCode);
                _runtime.StopHost(_options.ExitCode, _options.ShutdownTimeout);
                break;
            case CliServerRestartModes.Self:
            {
                var command = SelfCommand(_runtime);
                _runtime.StopHost(0, _options.ShutdownTimeout, () =>
                {
                    try
                    {
                        _runtime.Start(command);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Starting the server again failed");
                        _runtime.SetExitCode(1);
                    }
                    return Task.CompletedTask;
                });
                break;
            }
            default:
                _runtime.StopHost(0, _options.ShutdownTimeout, () => _options.Handler!(context));
                break;
        }
    }

    /// <summary>
    /// The command that starts this server again: the same executable and arguments; under the <c>dotnet</c> host
    /// (<c>dotnet run</c>, <c>dotnet app.dll</c>) <c>dotnet &lt;entry dll&gt; &lt;arguments&gt;</c>; in the same working directory.
    /// </summary>
    public static CliServerRestartCommand SelfCommand(ICliServerRestartRuntime runtime)
    {
        var args = runtime.CommandLineArgs;
        var executable = runtime.ProcessPath ?? (args.Count > 0 ? args[0] : throw new InvalidOperationException("no process path"));
        var arguments = args.Skip(1).ToList();
        // The file name on either kind of path (a Windows path read on any OS)
        var host = executable.Split('/', '\\')[^1];
        if (host.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || host.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var entry = runtime.EntryAssemblyPath ?? (args.Count > 0 ? args[0] : null)
                ?? throw new InvalidOperationException("no entry assembly");
            arguments.Insert(0, entry);
        }
        return new CliServerRestartCommand(executable, arguments, runtime.CurrentDirectory);
    }

    /// <summary>
    /// The first steps of the graceful shutdown: every WebSocket (events, logs, shells) closed with 1012, then the
    /// shutdown hooks in registration order (each up to 10 s; errors logged). The host's stop follows.
    /// </summary>
    public async Task ShutdownAsync()
    {
        try
        {
            // A client that never answers the close does not hold the restart up
            await CloseSocketsAsync().WaitAsync(SocketCloseTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Closing the WebSockets failed");
        }
        List<(string Name, Func<Task> Run)> hooks;
        lock (_lock) hooks = [.. _hooks];
        foreach (var (name, run) in hooks)
        {
            try
            {
                await run().WaitAsync(HookTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shutdown step {Name} failed", name);
            }
        }
    }

    private async Task CloseSocketsAsync()
    {
        if (_services == null) return;
        const int code = CliServerRestartConstants.CloseCode;
        const string reason = CliServerRestartConstants.CloseReason;
        if (_services.GetService<ICliEventSocketManager>() is { } events) await events.BroadcastDisconnectAsync(code, reason);
        if (_services.GetService<ICliLogSocketManager>() is { } logs) await logs.BroadcastDisconnectAsync(code, reason);
        _services.GetService<CliShellSessionRegistry>()?.TerminateAll(code, reason);
    }
}
