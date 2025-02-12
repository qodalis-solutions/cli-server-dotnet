namespace Qodalis.Cli.Abstractions;

public interface ICliCommandParameterDescriptor
{
    string Name { get; }

    IEnumerable<string>? Aliases { get; }

    string Description { get; }

    bool Required { get; }

    CommandParameterType Type { get; }

    object? DefaultValue { get; }

    CliProcessorMetadata? Metadata { get; set; }
}

public class CliProcessorMetadata : Dictionary<string, object>
{
    public bool? Sealed { get; set; }

    public bool? RequireServer { get; set; }

    public string? Module { get; set; }

    public string? Icon { get; set; }
}