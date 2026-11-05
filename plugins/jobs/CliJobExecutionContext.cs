using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// Default execution context provided to a job during execution.
/// </summary>
internal class CliJobExecutionContext : ICliJobExecutionContext
{
    public CliJobExecutionContext(CliJobLogger logger, IServiceProvider services)
    {
        Logger = logger;
        Services = services;
    }

    /// <inheritdoc />
    public ICliJobLogger Logger { get; }

    /// <inheritdoc />
    public IServiceProvider Services { get; }

    /// <inheritdoc />
    public IDictionary<string, object?> Properties { get; } = new Dictionary<string, object?>();

    /// <summary>Whether the job asked to leave this run out of the history.</summary>
    public bool Discarded { get; private set; }

    /// <inheritdoc />
    public void Discard() => Discarded = true;
}
