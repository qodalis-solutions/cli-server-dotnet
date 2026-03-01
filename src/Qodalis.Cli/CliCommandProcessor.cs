using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli;

public abstract class CliCommandProcessor : ICliCommandProcessor
{
    public abstract string Command { get; set; }
    public abstract string Description { get; set; }
    public virtual ICliCommandAuthor Author { get; set; } = DefaultLibraryAuthor.Instance;
    public virtual bool? AllowUnlistedCommands { get; set; }
    public virtual bool? ValueRequired { get; set; }
    public virtual string Version { get; set; } = "1.0.0";
    public virtual int ApiVersion { get; set; } = 1;

    public virtual IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    public virtual IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; }

    public abstract Task<string> HandleAsync
        (
        CliProcessCommand command,
        CancellationToken cancellationToken = default
        );
}
