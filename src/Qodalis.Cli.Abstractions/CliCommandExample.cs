namespace Qodalis.Cli.Abstractions;

/// <summary>An example command line shown in a command's help (<see cref="ICliCommandProcessor.Examples"/>).</summary>
/// <param name="Command">The command line, e.g. <c>hash sha256 hello</c>.</param>
/// <param name="Description">What it does.</param>
public sealed record CliCommandExample(string Command, string? Description = null);

/// <summary>Command name matching shared by the registry, permission checks and MCP.</summary>
public static class CliCommandNames
{
    /// <summary>Whether <paramref name="name"/> is the processor's command or one of its aliases (ignoring case).</summary>
    public static bool Matches(ICliCommandProcessor processor, string name) =>
        string.Equals(processor.Command, name, StringComparison.OrdinalIgnoreCase)
        || (processor.Aliases?.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) ?? false);

    /// <summary>
    /// The processor among <paramref name="processors"/> named <paramref name="name"/>: by command first, then by
    /// alias, so a command name wins over another command's alias.
    /// </summary>
    public static ICliCommandProcessor? Find(IEnumerable<ICliCommandProcessor>? processors, string name)
    {
        if (processors == null) return null;
        var list = processors as IReadOnlyCollection<ICliCommandProcessor> ?? processors.ToList();
        return list.FirstOrDefault(p => string.Equals(p.Command, name, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(p => Matches(p, name));
    }
}
