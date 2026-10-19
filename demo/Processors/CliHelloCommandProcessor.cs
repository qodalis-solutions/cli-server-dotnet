using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;

namespace Qodalis.Cli.Demo.Processors;

public class CliHelloCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "hello";
    public override bool? AcceptsRawInput { get; set; } = true;
    public override string Description { get; set; } = "Greets the user";

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "name",
            Aliases = ["-n"],
            Description = "Name to greet",
            Required = false,
            Type = CommandParameterType.String,
            DefaultValue = "World",
        }
    ];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var name = command.Args.TryGetValue("name", out var n) ? n.ToString() : CliValueHelpers.GetPositionalText(command) is { Length: > 0 } v ? v : "World";
        return Task.FromResult($"Hello, {name}!");
    }
}
