using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Server.Processors;

public class GuidCommandProcessor : Qodalis.Cli.CliCommandProcessor
{
    public override string Command { get; set; } = "guid";
    public override string Description { get; set; } = "Generates a new GUID";
    public override ICliCommandAuthor Author { get; set; } = null!;
    public override bool? AllowUnlistedCommands { get; set; }
    public override bool? ValueRequired { get; set; }

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "uppercase",
            Aliases = ["u"],
            Description = "Output the GUID in uppercase",
            Required = false,
            Type = CommandParameterType.Boolean,
        }
    ];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var guid = Guid.NewGuid().ToString();

        if (command.Args.TryGetValue("uppercase", out var val) && val is true or "true")
        {
            guid = guid.ToUpperInvariant();
        }

        return Task.FromResult(guid);
    }
}
