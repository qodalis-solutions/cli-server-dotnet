using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli;

/// <summary>
/// Abstract base class for CLI command processors, providing default property implementations.
/// Inherit from this class to create a concrete command processor.
/// </summary>
public abstract class CliCommandProcessor : ICliCommandProcessor
{
    /// <inheritdoc />
    public abstract string Command { get; set; }

    /// <inheritdoc />
    public abstract string Description { get; set; }

    /// <inheritdoc />
    public virtual ICliCommandAuthor Author { get; set; } = DefaultLibraryAuthor.Instance;

    /// <inheritdoc />
    public virtual bool? AllowUnlistedCommands { get; set; }

    /// <inheritdoc />
    public virtual bool? ValueRequired { get; set; }

    /// <inheritdoc />
    public virtual string Version { get; set; } = "1.0.0";

    /// <inheritdoc />
    public virtual IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    /// <inheritdoc />
    public virtual IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; }

    /// <inheritdoc />
    public abstract Task<string> HandleAsync
        (
        CliProcessCommand command,
        CancellationToken cancellationToken = default
        );
}
