using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;

namespace Qodalis.Cli.Demo.Processors;

public class CliEchoCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "echo";
    public override string Description { get; set; } = "Echoes back the input text";

    public override IEnumerable<CliCommandExample>? Examples { get; set; } =
    [
        new("echo hello world", "Prints hello world"),
    ];

    public override string? Documentation { get; set; } =
        "Prints its input back as the server received it. Quoted words keep their spaces: echo \"a  b\" prints a  b.";
    public override bool? ValueRequired { get; set; } = true;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        // `echo "a" "b"` prints a b, as the browser's echo does
        return Task.FromResult(CliValueHelpers.GetPositionalText(command));
    }
}
