using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Models;

/// <summary>
/// Data transfer object describing a registered command processor, used in the commands API response.
/// </summary>
public class CliServerCommandDescriptor
{
    /// <summary>Gets or sets the command keyword.</summary>
    public required string Command { get; set; }

    /// <summary>Gets or sets the command description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the command version.</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets the API version this command targets.</summary>
    public int? ApiVersion { get; set; }

    /// <summary>Gets or sets the parameter descriptors for this command.</summary>
    public List<CliCommandParameterDescriptorDto>? Parameters { get; set; }

    /// <summary>Gets or sets nested sub-command descriptors.</summary>
    public List<CliServerCommandDescriptor>? Processors { get; set; }
}

/// <summary>
/// Data transfer object for a command parameter descriptor, used in API responses.
/// </summary>
public class CliCommandParameterDescriptorDto
{
    /// <summary>Gets or sets the parameter name.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the parameter aliases.</summary>
    public IEnumerable<string>? Aliases { get; set; }

    /// <summary>Gets or sets the parameter description.</summary>
    public required string Description { get; set; }

    /// <summary>Gets or sets whether this parameter is required.</summary>
    public bool Required { get; set; }

    /// <summary>Gets or sets the parameter data type.</summary>
    public CommandParameterType Type { get; set; }

    /// <summary>Gets or sets the default value.</summary>
    public object? DefaultValue { get; set; }
}
