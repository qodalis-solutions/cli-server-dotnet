using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Demo.Processors;

public class CliStatusCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "status";
    public override string Description { get; set; } = "Shows server status information";

    public override IEnumerable<CliCommandExample>? Examples { get; set; } =
    [
        new("status", "Uptime, version and state of the server"),
    ];

    public override string? Documentation { get; set; } =
        "Shows how long the server has been running, its version and its current state.";

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var status = $"Server: Running\nUptime: {Environment.TickCount64 / 1000}s\nOS: {Environment.OSVersion}\n.NET: {Environment.Version}";
        return Task.FromResult(status);
    }
}
