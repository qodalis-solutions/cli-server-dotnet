using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Demo.Processors;

public class CliHelloCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "hello";
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
        var name = command.Args.TryGetValue("name", out var n) ? n.ToString() : command.Value ?? "World";
        return Task.FromResult($"Hello, {name}!");
    }
}
