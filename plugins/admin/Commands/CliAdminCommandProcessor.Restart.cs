using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Restart;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Admin.Commands;

public sealed partial class CliAdminCommandProcessor
{
    /// <summary>The pending changes as lines: <c>  - CORS: Mode changed (config, since 2026-11-25 10:04:05)</c>.</summary>
    private static IEnumerable<string> PendingLines(CliServerRestartStatus status)
    {
        if (status.Pending.Count == 0) return ["No changes are waiting for a restart."];
        return new[] { $"{status.Pending.Count} change(s) take effect after a restart:" }
            .Concat(status.Pending.Select(p => $"  - {p.Description} ({p.Source}, since {AdminFormat.ShortTime(p.Since)})"));
    }

    /// <summary>How the server restarts, in one line.</summary>
    private static string ModeLine(CliServerRestartStatus status)
    {
        if (!status.Supported) return $"Restart from here: not supported ({status.Reason ?? "no mode"})";
        var how = status.Mode switch
        {
            CliServerRestartModes.Exit => $"exits for {status.Supervisor ?? "the supervisor"} to start it again",
            CliServerRestartModes.Self => "starts itself again",
            _ => "the host's handler",
        };
        return $"Restart mode: {status.Mode} ({how})";
    }

    private sealed class RestartStatusProcessor(ICliServerRestart restart) : CliCommandProcessor
    {
        public override string Command { get; set; } = "status";
        public override string Description { get; set; } =
            "Whether the server can restart itself, how, since when it runs, and the changes waiting for a restart";
        public override string? Module { get; set; } = ModuleName;
        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = [JsonParam];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        {
            var status = restart.Status();
            if (Flag(command, "json")) return Task.FromResult(JsonSerializer.Serialize(status, Json));
            return Task.FromResult(string.Join('\n',
                new[] { $"Running since {AdminFormat.ShortTime(status.StartedAt)}", ModeLine(status) }.Concat(PendingLines(status))));
        }
    }

    /// <summary>
    /// <c>admin restart [--yes] [--reason &lt;text&gt;]</c>: restarts the server (<c>server:restart</c>). Server commands
    /// cannot ask, so without <c>--yes</c> it prints what is pending and how to confirm. <c>admin restart status</c> needs
    /// only <c>admin:access</c>.
    /// </summary>
    private sealed class RestartProcessor : CliCommandProcessor
    {
        private readonly ICliServerRestart _restart;
        private readonly IServiceProvider _services;

        public RestartProcessor(ICliServerRestart restart, IServiceProvider services)
        {
            _restart = restart;
            _services = services;
            Processors = [new RestartStatusProcessor(restart)];
        }

        public override string Command { get; set; } = "restart";
        public override string Description { get; set; } =
            "Restarts the server (needs --yes and server:restart); restart status shows the changes waiting for it";
        public override string? Module { get; set; } = ModuleName;

        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
        [
            new CliCommandParameterDescriptor
            {
                Name = "yes",
                Aliases = ["y"],
                Description = "Restart now (without it, only shows what is pending)",
                Type = CommandParameterType.Boolean,
            },
            new CliCommandParameterDescriptor { Name = "reason", Description = "Why, kept in the audit log", Type = CommandParameterType.String },
        ];

        public override IEnumerable<CliCommandExample>? Examples { get; set; } =
        [
            new("admin restart", "What is pending, and how to confirm"),
            new("admin restart --yes --reason \"apply CORS\"", "Restart now"),
            new("admin restart status", "Mode, supervisor, start time, pending changes"),
        ];

        public override async Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        {
            var status = _restart.Status();
            var caller = _services.GetService<IHttpContextAccessor>()?.HttpContext?.GetCliPrincipal();
            // Checked here, not as RequiredPermissions: admin restart status needs only admin:access
            if (caller != null && !caller.Can(CliRoutePolicy.ServerRestart))
                return $"Error: forbidden: missing permission {CliRoutePolicy.ServerRestart}";
            if (!status.Supported)
                return string.Join('\n', new[] { $"Error: restart not supported: {status.Reason}" }.Concat(PendingLines(status)));
            if (!Flag(command, "yes") && !Flag(command, "y"))
            {
                return string.Join('\n', PendingLines(status)
                    .Append(ModeLine(status))
                    .Append("Run admin restart --yes [--reason \"<why>\"] to restart the server now."));
            }
            var reason = command.Args.TryGetValue("reason", out var raw) ? raw?.ToString()?.Trim() : null;
            try
            {
                var result = await _restart.RestartAsync(new CliServerRestartRequest
                {
                    Reason = string.IsNullOrEmpty(reason) ? null : reason,
                    Actor = caller != null ? new CliServerRestartActor(caller.UserId, caller.UserName) : null,
                    Via = "command",
                }, cancellationToken);
                var applied = result.Pending.Count > 0 ? $" to apply {result.Pending.Count} pending change(s)" : "";
                return $"Restarting the server{applied} ({status.Mode} mode). The connection drops and comes back once it runs again.";
            }
            catch (CliServerRestartException ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }
}
