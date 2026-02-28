using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Server.Processors;

public class EchoCommandProcessor : Qodalis.Cli.CliCommandProcessor
{
    public override string Command { get; set; } = "echo";
    public override string Description { get; set; } = "Echoes back the input text";
    public override ICliCommandAuthor Author { get; set; } = null!;
    public override bool? AllowUnlistedCommands { get; set; }
    public override bool? ValueRequired { get; set; } = true;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(command.Value ?? "");
    }
}
