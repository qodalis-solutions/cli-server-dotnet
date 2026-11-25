using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Plugin.Admin.Commands;



/// <summary>
/// The admin module: describes the <c>admin</c> command (registered on its own with dependency injection, joined by
/// its <see cref="ICliCommandProcessor.Module"/>) on the dashboard's plugins page and in <c>admin plugins</c>.
/// </summary>
public sealed class AdminModule : CliModule
{
    public override string Name => CliAdminCommandProcessor.ModuleName;

    public override string Version { get; } = Qodalis.Cli.Models.CliInstalledPlugin.FromAssembly(typeof(AdminModule).Assembly).Version ?? "1.0.0";

    public override string Description => "Server administration from the terminal: status, command modules, WebSocket clients, logs";

    public override string? Icon => "🛠️";

    public override IEnumerable<ICliCommandProcessor> Processors => [];
}
