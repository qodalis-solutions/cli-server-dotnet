namespace Qodalis.Cli.Abstractions;

/// <summary>
/// Default implementation of <see cref="ICliCommandParameterDescriptor"/> that describes a CLI command parameter.
/// </summary>
public class CliCommandParameterDescriptor : ICliCommandParameterDescriptor
{
    /// <inheritdoc />
    public required string Name { get; set; }

    /// <inheritdoc />
    public IEnumerable<string>? Aliases { get; set; }

    /// <inheritdoc />
    public required string Description { get; set; }

    /// <inheritdoc />
    public bool Required { get; set; }

    /// <inheritdoc />
    public CommandParameterType Type { get; set; }

    /// <inheritdoc />
    public object? DefaultValue { get; set; }

    /// <inheritdoc />
    public CliProcessorMetadata? Metadata { get; set; }
}
