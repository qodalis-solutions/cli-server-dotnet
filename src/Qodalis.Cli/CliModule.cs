using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Config;

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
    public virtual string? Icon => null;
    public virtual IEnumerable<ICliConfigProvider> ConfigProviders => [];
    public virtual IEnumerable<CliModulePermission> Permissions => [];
}
