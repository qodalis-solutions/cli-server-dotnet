using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Server.Processors;

public class CliUuidCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "uuid";
    public override string Description { get; set; } = "Generates random UUIDs";

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "count",
            Aliases = ["-n"],
            Description = "Number of UUIDs to generate (max 50)",
            Type = CommandParameterType.Number,
            DefaultValue = "1",
        },
    ];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var count = command.Args.TryGetValue("count", out var c) ? int.Parse(c.ToString()!) : 1;
        count = Math.Clamp(count, 1, 50);

        var uuids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid().ToString()).ToArray();
        return Task.FromResult(string.Join("\n", uuids));
    }
}
