namespace Qodalis.Cli.Abstractions.Jobs;

/// <summary>
/// Provides persistent storage for job execution history and job state.
/// Implement this interface to use a database or other durable store instead of in-memory storage.
/// </summary>
public interface ICliJobStorageProvider
{
    /// <summary>
    /// Persists a job execution record (insert or update).
    /// </summary>
    /// <param name="execution">The execution record to save.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SaveExecutionAsync(JobExecution execution, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a paginated list of executions for a specific job.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="offset">Number of records to skip.</param>
    /// <param name="statusFilter">Optional status filter (e.g., "Running", "Failed").</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A tuple of the matching executions and the total count.</returns>
    Task<(List<JobExecution> Items, int Total)> GetExecutionsAsync(string jobId, int limit = 20, int offset = 0, string? statusFilter = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the executions of several jobs (or of every job), newest first. The default merges
    /// <see cref="GetExecutionsAsync"/> per job and needs <see cref="JobExecutionQuery.JobIds"/>; override it to query
    /// the store once.
    /// </summary>
    async Task<(IReadOnlyList<JobExecution> Items, int Total)> QueryExecutionsAsync(JobExecutionQuery query, CancellationToken cancellationToken = default)
    {
        if (query.JobIds == null)
            throw new NotSupportedException("Listing the executions of every job is not supported by this store");

        return await query.MergeAsync(query.JobIds, async (jobId, limit, status) =>
        {
            var (items, total) = await GetExecutionsAsync(jobId, limit, 0, status, cancellationToken);
            return (items, total);
        });
    }

    /// <summary>
    /// Deletes the executions (and their logs) that started before <paramref name="before"/>; returns how many. Run
    /// it on a schedule to keep the history bounded. The default keeps everything and returns 0.
    /// </summary>
    Task<int> PruneAsync(DateTime before, CancellationToken cancellationToken = default) => Task.FromResult(0);

    /// <summary>
    /// Deletes an execution (a run the job discarded, see <see cref="ICliJobExecutionContext.Discard"/>); returns
    /// whether it was deleted. The default keeps every execution and returns <c>false</c>.
    /// </summary>
    Task<bool> DeleteExecutionAsync(string executionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>
    /// Retrieves a single execution by its unique identifier.
    /// </summary>
    /// <param name="executionId">The execution identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The execution record, or <c>null</c> if not found.</returns>
    Task<JobExecution?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the current state (active, paused, stopped) for a job.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="state">The state to save.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SaveJobStateAsync(string jobId, JobState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the current state for a specific job.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The job state, or <c>null</c> if no state has been saved.</returns>
    Task<JobState?> GetJobStateAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the current state for all known jobs.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A dictionary mapping job identifiers to their current state.</returns>
    Task<Dictionary<string, JobState>> GetAllJobStatesAsync(CancellationToken cancellationToken = default);
}
