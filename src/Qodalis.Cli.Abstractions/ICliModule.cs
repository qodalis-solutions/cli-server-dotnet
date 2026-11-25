using Qodalis.Cli.Abstractions.Config;

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

    /// <summary>
    /// One emoji or an image URL shown for the module (the admin dashboard's plugins page); <c>null</c> = none.
    /// </summary>
    string? Icon => null;

    /// <summary>
    /// Config providers the module brings (its settings), registered with the module (<c>cli.AddModule</c>).
    /// </summary>
    IEnumerable<ICliConfigProvider> ConfigProviders => [];

    /// <summary>
    /// Permissions its commands require (<see cref="ICliCommandProcessor.RequiredPermissions"/>), added to the
    /// server's permission catalog with the module (<c>cli.AddModule</c>, surface <c>cli</c>); a name already in the
    /// catalog is left as registered.
    /// </summary>
    IEnumerable<CliModulePermission> Permissions => [];
}

/// <summary>A permission a module brings to the server's permission catalog (<see cref="ICliModule.Permissions"/>).</summary>
/// <param name="Name">The permission, <c>resource:action</c> (e.g. <c>aws:read</c>).</param>
/// <param name="Description">What it allows, shown in the permission pickers.</param>
/// <param name="Group">The picker group it is listed under (e.g. <c>AWS</c>).</param>
public sealed record CliModulePermission(string Name, string Description, string Group);
