namespace Qodalis.Cli.Abstractions.Jobs;

/// <summary>
/// The jobs that the jobs API (<c>/api/v1/qcli/jobs</c>), the <c>jobs_*</c> MCP tools and the dashboard show and
/// control. The default manager runs the jobs registered with <c>AddJob</c> in this process; a host whose jobs run
/// elsewhere (another service, a queue, its own scheduler) implements this interface and registers it with
/// <c>SetJobManager</c>, so the CLI and the dashboard work on those jobs instead.
/// </summary>
/// <remarks>
/// Errors: throw <see cref="KeyNotFoundException"/> for an unknown job (404), <see cref="InvalidOperationException"/>
/// when the job's state refuses the action (409, e.g. already paused) and <see cref="NotSupportedException"/> for an
/// action the manager does not offer (501). List the actions a job offers in <see cref="CliJobInfo.Actions"/>.
/// </remarks>
public interface ICliJobManager
{
    /// <summary>Every job with its status and last run.</summary>
    Task<IReadOnlyList<CliJobInfo>> GetJobsAsync(CancellationToken cancellationToken);

    /// <summary>One job, or <c>null</c> when there is no job with that id.</summary>
    async Task<CliJobInfo?> GetJobAsync(string jobId, CancellationToken cancellationToken) =>
        (await GetJobsAsync(cancellationToken)).FirstOrDefault(j => j.Id == jobId);

    /// <summary>
    /// Runs the job now, or queues it (overlap policy <c>Queue</c>, or a manager whose jobs run elsewhere). Throw
    /// <see cref="InvalidOperationException"/> when the job is running and the run is refused (409).
    /// </summary>
    Task TriggerAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the job now like <see cref="TriggerAsync"/> and tells whether the run started or was queued (overlap
    /// policy <c>Queue</c>, every slot taken); the jobs API and MCP answer "Job queued" for the latter. The default
    /// calls <see cref="TriggerAsync"/> and reports <see cref="CliJobTriggerOutcome.Started"/>.
    /// </summary>
    async Task<CliJobTriggerOutcome> TriggerWithOutcomeAsync(string jobId, CancellationToken cancellationToken)
    {
        await TriggerAsync(jobId, cancellationToken);
        return CliJobTriggerOutcome.Started;
    }

    /// <summary>Pauses the job's schedule.</summary>
    Task PauseAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Resumes a paused job's schedule.</summary>
    Task ResumeAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Stops the job: cancels a running execution and disables its schedule.</summary>
    Task StopAsync(string jobId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Stopping jobs is not supported");

    /// <summary>Cancels every running execution of the job and drops a queued run.</summary>
    Task CancelAsync(string jobId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Cancelling a running job is not supported");

    /// <summary>Changes the job's options (schedule, retries, timeout, ...).</summary>
    Task UpdateAsync(string jobId, Action<CliJobOptions> update, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Changing a job's options is not supported");

    /// <summary>The job's executions, newest first, optionally only those with <paramref name="status"/>.</summary>
    Task<(IReadOnlyList<JobExecution> Items, int Total)> GetExecutionsAsync(
        string jobId, int limit, int offset, string? status, CancellationToken cancellationToken);

    /// <summary>
    /// The executions of several jobs (or of every job), newest first. The default merges
    /// <see cref="GetExecutionsAsync"/> per job.
    /// </summary>
    async Task<(IReadOnlyList<JobExecution> Items, int Total)> QueryExecutionsAsync(
        JobExecutionQuery query, CancellationToken cancellationToken)
    {
        var jobIds = query.JobIds ?? (await GetJobsAsync(cancellationToken)).Select(j => j.Id).ToList();
        return await query.MergeAsync(jobIds, (jobId, limit, status) =>
            GetExecutionsAsync(jobId, limit, 0, status, cancellationToken));
    }

    /// <summary>One execution of the job with its logs, or <c>null</c>.</summary>
    Task<JobExecution?> GetExecutionAsync(string jobId, string executionId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a finished execution of the job and its logs. Throw <see cref="KeyNotFoundException"/> for an unknown
    /// job or execution and <see cref="InvalidOperationException"/> for a running one.
    /// </summary>
    Task DeleteExecutionAsync(string jobId, string executionId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Deleting runs is not supported");

    /// <summary>
    /// Deletes the job's finished executions, or only those with <paramref name="status"/>; returns how many. Running
    /// executions are kept. The default pages through <see cref="GetExecutionsAsync"/> and deletes each one with
    /// <see cref="DeleteExecutionAsync"/>.
    /// </summary>
    async Task<int> DeleteExecutionsAsync(string jobId, string? status, CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        var deleted = 0;
        var offset = 0;
        while (true)
        {
            var (items, _) = await GetExecutionsAsync(jobId, pageSize, offset, status, cancellationToken);
            if (items.Count == 0) return deleted;
            var kept = 0;
            foreach (var execution in items)
            {
                if (execution.Status == JobExecutionStatus.Running)
                {
                    kept++;
                    continue;
                }
                await DeleteExecutionAsync(jobId, execution.Id, cancellationToken);
                deleted++;
            }
            // Deleted runs leave the list, so the next page starts after the kept ones.
            offset += kept;
            if (items.Count < pageSize) return deleted;
        }
    }
}

/// <summary>A job as <see cref="ICliJobManager"/> reports it.</summary>
public class CliJobInfo
{
    /// <summary>Gets or sets the job's id (used in the API's routes).</summary>
    public required string Id { get; set; }

    /// <summary>Gets or sets the job's display name.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets what the job does.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the group the job is listed under.</summary>
    public string? Group { get; set; }

    /// <summary>Gets or sets the scheduling status.</summary>
    public JobStatus Status { get; set; } = JobStatus.Active;

    /// <summary>Gets or sets the cron expression, when the job runs on one.</summary>
    public string? Schedule { get; set; }

    /// <summary>Gets or sets the interval, when the job runs on one.</summary>
    public TimeSpan? Interval { get; set; }

    /// <summary>Gets or sets the retries after a failed run.</summary>
    public int MaxRetries { get; set; }

    /// <summary>Gets or sets the delay before a retry.</summary>
    public TimeSpan RetryDelay { get; set; }

    /// <summary>Gets or sets how the retry delay grows.</summary>
    public JobRetryStrategy RetryStrategy { get; set; } = JobRetryStrategy.Fixed;

    /// <summary>Gets or sets the longest a run may take.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Gets or sets what happens when a run is due while one is running.</summary>
    public JobOverlapPolicy OverlapPolicy { get; set; } = JobOverlapPolicy.Skip;

    /// <summary>Gets or sets how many runs of the job may go at the same time. A manager without the option reports 1.</summary>
    public int MaxConcurrentRuns { get; set; } = 1;

    /// <summary>Gets or sets the newest executing attempt's id, if one is executing.</summary>
    public string? CurrentExecutionId { get; set; }

    /// <summary>
    /// Gets or sets the ids of the attempts executing now, oldest first; empty when idle. A run waiting for its retry
    /// keeps its slot but has no entry. When a manager leaves it empty but sets <see cref="CurrentExecutionId"/>, the
    /// API reports that one.
    /// </summary>
    public IReadOnlyList<string> RunningExecutionIds { get; set; } = [];

    /// <summary>Gets or sets whether a run is queued, waiting for a free slot.</summary>
    public bool Queued { get; set; }

    /// <summary>Gets or sets the next scheduled run (UTC).</summary>
    public DateTime? NextRunAt { get; set; }

    /// <summary>Gets or sets when the job last ran (UTC).</summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>Gets or sets how the last run ended (<c>completed</c>, <c>failed</c>, ...).</summary>
    public string? LastRunStatus { get; set; }

    /// <summary>Gets or sets how long the last run took, in milliseconds.</summary>
    public long? LastRunDuration { get; set; }

    /// <summary>
    /// Gets or sets the actions this job offers (<see cref="CliJobActions"/>); <c>null</c> means all of them.
    /// Clients hide the others.
    /// </summary>
    public IReadOnlyList<string>? Actions { get; set; }
}

/// <summary>What a manual trigger did: started a run, or queued one that starts when a slot frees.</summary>
public enum CliJobTriggerOutcome
{
    /// <summary>A run started.</summary>
    Started,
    /// <summary>Every slot was taken and the overlap policy is <c>Queue</c>: the run waits for a free slot.</summary>
    Queued,
}

/// <summary>Names of the job actions, for <see cref="CliJobInfo.Actions"/>.</summary>
public static class CliJobActions
{
    public const string Trigger = "trigger";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Stop = "stop";
    public const string Cancel = "cancel";
    public const string Update = "update";
    public const string DeleteRuns = "delete-runs";
}
