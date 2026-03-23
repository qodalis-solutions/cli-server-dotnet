namespace Qodalis.Cli.Abstractions.Jobs;

/// <summary>
/// Defines a scheduled background job that can be executed by the CLI job scheduler.
/// </summary>
public interface ICliJob
{
    /// <summary>
    /// Executes the job logic.
    /// </summary>
    /// <param name="context">The execution context providing access to the job logger.</param>
    /// <param name="cancellationToken">A token to cancel the job execution.</param>
    Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken);
}
