using Qodalis.Cli.Restart;

namespace Qodalis.Cli.Extensions;

public partial class CliBuilder
{
    /// <summary>Options of restarting the server (<see cref="ConfigureRestart"/>).</summary>
    public CliServerRestartOptions RestartOptions { get; } = new();

    /// <summary>
    /// How the server restarts from the dashboard, <c>admin restart</c> or <c>POST /api/v1/qcli/server/restart</c>:
    /// <c>Mode</c> <c>auto</c> (default: <c>exit</c> under a detected supervisor, else not supported), <c>exit</c> (the
    /// host stops and the process exits with <c>ExitCode</c>, default 75, for Docker, systemd, PM2, Kubernetes or IIS
    /// to start it again), <c>self</c> (the same command started again once the host stopped: bare runs and
    /// <c>dotnet run</c>) or <c>handler</c> (the host's function); <c>AllowAnonymous</c> on a server without sign-in,
    /// <c>ShutdownTimeout</c> (default 10 s). Throws <see cref="ArgumentException"/> for an invalid mode or
    /// <c>handler</c> without a handler.
    /// </summary>
    /// <example>
    /// <code>
    /// cli.ConfigureRestart(o =&gt; o.Mode = CliServerRestartModes.Self);
    /// </code>
    /// </example>
    public CliBuilder ConfigureRestart(Action<CliServerRestartOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(RestartOptions);
        RestartOptions.Validate();
        return this;
    }
}
