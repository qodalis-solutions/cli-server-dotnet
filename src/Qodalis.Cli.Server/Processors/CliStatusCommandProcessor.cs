using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Server.Processors;

/// <summary>
/// Command processor that displays server status information including uptime and runtime version.
/// </summary>
public class CliStatusCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "status";
    public override string Description { get; set; } = "Shows server status information";

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var status = $"Server: Running\nUptime: {Environment.TickCount64 / 1000}s\nOS: {Environment.OSVersion}\n.NET: {Environment.Version}";
        return Task.FromResult(status);
    }
}
