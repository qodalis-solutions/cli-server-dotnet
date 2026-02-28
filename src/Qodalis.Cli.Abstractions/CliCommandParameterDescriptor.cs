namespace Qodalis.Cli.Abstractions;

public class CliCommandParameterDescriptor : ICliCommandParameterDescriptor
{
    public required string Name { get; set; }
    public IEnumerable<string>? Aliases { get; set; }
    public required string Description { get; set; }
    public bool Required { get; set; }
    public CommandParameterType Type { get; set; }
    public object? DefaultValue { get; set; }
    public CliProcessorMetadata? Metadata { get; set; }
}
