using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Demo.Processors;

public class CliEchoCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "echo";
    public override string Description { get; set; } = "Echoes back the input text";
    public override bool? ValueRequired { get; set; } = true;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(command.Value ?? "");
    }
}
