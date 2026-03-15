using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs;

internal class CliJobExecutionContext : ICliJobExecutionContext
{
    public CliJobExecutionContext(CliJobLogger logger)
    {
        Logger = logger;
    }

    public ICliJobLogger Logger { get; }
}
