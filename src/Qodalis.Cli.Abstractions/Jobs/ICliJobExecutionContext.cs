namespace Qodalis.Cli.Abstractions.Jobs;

/// <summary>
/// Provides contextual services to a running job, such as logging.
/// </summary>
public interface ICliJobExecutionContext
{
    /// <summary>
    /// Gets the logger for recording job execution messages.
    /// </summary>
    ICliJobLogger Logger { get; }

    /// <summary>
    /// The services of a scope created for this run (disposed when it ends), to resolve scoped services such as a
    /// <c>DbContext</c> from a job registered as an instance. Without a host service provider, an empty one.
    /// </summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// The execution's own values (<see cref="JobExecution.Properties"/>), e.g. <c>Properties["items"] = 32</c>;
    /// saved when the run ends, whatever its outcome.
    /// </summary>
    IDictionary<string, object?> Properties { get; }

    /// <summary>
    /// Leaves this run out of the history when it completes: for a frequent job whose run found nothing to do
    /// (e.g. no digest was due). The job's last run is still updated. Failed runs are always kept.
    /// </summary>
    void Discard();
}
