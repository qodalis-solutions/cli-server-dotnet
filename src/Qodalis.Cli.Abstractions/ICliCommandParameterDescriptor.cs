namespace Qodalis.Cli.Abstractions;

/// <summary>
/// Describes a parameter accepted by a CLI command processor.
/// </summary>
public interface ICliCommandParameterDescriptor
{
    /// <summary>
    /// Gets the parameter name (e.g., "algorithm", "output").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets optional short aliases for the parameter (e.g., "-a", "-o").
    /// </summary>
    IEnumerable<string>? Aliases { get; }

    /// <summary>
    /// Gets a human-readable description of what the parameter does.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets whether this parameter is required for command execution.
    /// </summary>
    bool Required { get; }

    /// <summary>
    /// Gets the data type of the parameter value.
    /// </summary>
    CommandParameterType Type { get; }

    /// <summary>
    /// Gets the default value used when the parameter is not provided.
    /// </summary>
    object? DefaultValue { get; }

    /// <summary>
    /// Gets or sets additional metadata associated with this parameter.
    /// </summary>
    CliProcessorMetadata? Metadata { get; set; }
}

/// <summary>
/// A dictionary-based metadata container for CLI command processors, providing
/// well-known properties such as <see cref="Sealed"/> and <see cref="RequireServer"/>.
/// </summary>
public class CliProcessorMetadata : Dictionary<string, object>
{
    /// <summary>
    /// Gets or sets whether the processor is sealed and cannot be extended.
    /// </summary>
    public bool? Sealed { get; set; }

    /// <summary>
    /// Gets or sets whether the processor requires a server connection to operate.
    /// </summary>
    public bool? RequireServer { get; set; }

    /// <summary>
    /// Gets or sets the name of the module this processor belongs to.
    /// </summary>
    public string? Module { get; set; }

    /// <summary>
    /// Gets or sets the icon identifier for the processor.
    /// </summary>
    public string? Icon { get; set; }
}