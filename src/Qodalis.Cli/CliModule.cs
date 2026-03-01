using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli;

/// <summary>
/// Base class for CLI modules providing sensible defaults.
/// </summary>
public abstract class CliModule : ICliModule
{
    public abstract string Name { get; }
    public abstract string Version { get; }
    public abstract string Description { get; }
    public virtual ICliCommandAuthor Author { get; } = DefaultLibraryAuthor.Instance;
    public abstract IEnumerable<ICliCommandProcessor> Processors { get; }
}
