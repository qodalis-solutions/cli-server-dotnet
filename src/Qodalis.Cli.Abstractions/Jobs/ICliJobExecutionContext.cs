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
}
