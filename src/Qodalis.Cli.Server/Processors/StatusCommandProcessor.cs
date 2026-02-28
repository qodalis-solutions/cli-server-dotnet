using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Server.Processors;

public class StatusCommandProcessor : Qodalis.Cli.CliCommandProcessor
{
    public override string Command { get; set; } = "status";
    public override string Description { get; set; } = "Shows server status information";
    public override ICliCommandAuthor Author { get; set; } = null!;
    public override bool? AllowUnlistedCommands { get; set; }
    public override bool? ValueRequired { get; set; }

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var status = $"Server: Running\nUptime: {Environment.TickCount64 / 1000}s\nOS: {Environment.OSVersion}\n.NET: {Environment.Version}";
        return Task.FromResult(status);
    }
}
