using System.Text.Json.Serialization;
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

    /// <summary>Other names of the command.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Aliases { get; set; }

    /// <summary>Example command lines.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CliCommandExampleDto>? Examples { get; set; }

    /// <summary>Longer help text (paragraphs separated by a blank line).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Documentation { get; set; }

    /// <summary>The group the command is listed under in help.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Module { get; set; }

    /// <summary>One emoji for help.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; set; }

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

    /// <summary>The only values the parameter takes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AllowedValues { get; set; }
}

/// <summary>An example command line in a command's help.</summary>
public class CliCommandExampleDto
{
    public required string Command { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}
