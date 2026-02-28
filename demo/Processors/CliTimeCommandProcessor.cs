using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Demo.Processors;

public class CliTimeCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "time";
    public override string Description { get; set; } = "Shows the current server date and time";

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "utc",
            Description = "Show time in UTC",
            Required = false,
            Type = CommandParameterType.Boolean,
        },
        new CliCommandParameterDescriptor
        {
            Name = "format",
            Aliases = ["-f"],
            Description = "Date/time format string",
            Required = false,
            Type = CommandParameterType.String,
            DefaultValue = "yyyy-MM-dd HH:mm:ss",
        }
    ];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var useUtc = command.Args.ContainsKey("utc");
        var format = command.Args.TryGetValue("format", out var fmt) ? fmt.ToString() : "yyyy-MM-dd HH:mm:ss";

        var now = useUtc ? DateTime.UtcNow : DateTime.Now;
        var label = useUtc ? "UTC" : "Local";

        return Task.FromResult($"{label}: {now.ToString(format)}");
    }
}
