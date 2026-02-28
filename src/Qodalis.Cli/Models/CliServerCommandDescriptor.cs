using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Models;

public class CliServerCommandDescriptor
{
    public required string Command { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }
    public List<CliCommandParameterDescriptorDto>? Parameters { get; set; }
    public List<CliServerCommandDescriptor>? Processors { get; set; }
}

public class CliCommandParameterDescriptorDto
{
    public required string Name { get; set; }
    public IEnumerable<string>? Aliases { get; set; }
    public required string Description { get; set; }
    public bool Required { get; set; }
    public CommandParameterType Type { get; set; }
    public object? DefaultValue { get; set; }
}
