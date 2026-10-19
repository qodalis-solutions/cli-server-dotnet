using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;

namespace Qodalis.Cli.Server.Processors;

/// <summary>
/// Command processor that echoes back the input text, as a shell's echo prints it.
/// </summary>
public class CliEchoCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "echo";
    public override string Description { get; set; } = "Echoes back the input text";
    public override bool? ValueRequired { get; set; } = true;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        // `echo "a" "b"` prints a b, as the browser's echo does
        return Task.FromResult(CliValueHelpers.JoinShellWords(command.Value));
    }
}
