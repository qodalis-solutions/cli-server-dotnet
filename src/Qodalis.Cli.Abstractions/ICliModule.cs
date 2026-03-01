namespace Qodalis.Cli.Abstractions;

/// <summary>
/// Represents a module that bundles one or more command processors.
/// </summary>
public interface ICliModule
{
    /// <summary>
    /// Unique name of the module.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Module version.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Short description of the module.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Author of the module.
    /// </summary>
    ICliCommandAuthor Author { get; }

    /// <summary>
    /// Command processors provided by this module.
    /// </summary>
    IEnumerable<ICliCommandProcessor> Processors { get; }
}
