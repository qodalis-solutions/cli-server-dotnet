using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// Default execution context provided to a job during execution.
/// </summary>
internal class CliJobExecutionContext : ICliJobExecutionContext
{
    public CliJobExecutionContext(CliJobLogger logger)
    {
        Logger = logger;
    }

    /// <inheritdoc />
    public ICliJobLogger Logger { get; }
}
