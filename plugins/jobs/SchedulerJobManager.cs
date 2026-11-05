using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// The default <see cref="ICliJobManager"/>: the jobs registered with <c>AddJob</c>, run in this process by
/// <see cref="CliJobScheduler"/>, with their history in the <see cref="ICliJobStorageProvider"/>.
/// </summary>
public sealed class SchedulerJobManager : ICliJobManager
{
    private readonly ICliJobScheduler _scheduler;
    private readonly ICliJobStorageProvider _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchedulerJobManager"/> class.
    /// </summary>
    public SchedulerJobManager(ICliJobScheduler scheduler, ICliJobStorageProvider storage)
    {
        _scheduler = scheduler;
        _storage = storage;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CliJobInfo>> GetJobsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CliJobInfo>>(_scheduler.Jobs.Values.Select(ToInfo).ToList());

    /// <inheritdoc />
    public Task<CliJobInfo?> GetJobAsync(string jobId, CancellationToken cancellationToken) =>
        Task.FromResult(_scheduler.Jobs.TryGetValue(jobId, out var reg) ? ToInfo(reg) : null);

    /// <inheritdoc />
    public Task TriggerAsync(string jobId, CancellationToken cancellationToken) =>
        _scheduler.TriggerAsync(jobId, cancellationToken);

    /// <inheritdoc />
    public Task PauseAsync(string jobId, CancellationToken cancellationToken) =>
        _scheduler.PauseAsync(jobId, cancellationToken);

    /// <inheritdoc />
    public Task ResumeAsync(string jobId, CancellationToken cancellationToken) =>
        _scheduler.ResumeAsync(jobId, cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(string jobId, CancellationToken cancellationToken) =>
        _scheduler.StopJobAsync(jobId, cancellationToken);

    /// <inheritdoc />
    public Task CancelAsync(string jobId, CancellationToken cancellationToken) =>
        _scheduler.CancelCurrentAsync(jobId, cancellationToken);

    /// <inheritdoc />
    public Task UpdateAsync(string jobId, Action<CliJobOptions> update, CancellationToken cancellationToken) =>
        _scheduler.UpdateOptionsAsync(jobId, update, cancellationToken);

    /// <inheritdoc />
    public async Task<(IReadOnlyList<JobExecution> Items, int Total)> GetExecutionsAsync(
        string jobId, int limit, int offset, string? status, CancellationToken cancellationToken)
    {
        var (items, total) = await _storage.GetExecutionsAsync(jobId, limit, offset, status, cancellationToken);
        return (items, total);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Without job ids the query names this scheduler's jobs, so a store without its own query (the default merges
    /// the per-job listings) still answers.
    /// </remarks>
    public Task<(IReadOnlyList<JobExecution> Items, int Total)> QueryExecutionsAsync(
        JobExecutionQuery query, CancellationToken cancellationToken)
    {
        query.JobIds ??= [.. _scheduler.Jobs.Keys];
        return _storage.QueryExecutionsAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<JobExecution?> GetExecutionAsync(string jobId, string executionId, CancellationToken cancellationToken)
    {
        var execution = await _storage.GetExecutionAsync(executionId, cancellationToken);
        return execution?.JobId == jobId ? execution : null;
    }

    internal static CliJobInfo ToInfo(JobRegistration r) => new()
    {
        Id = r.Id,
        Name = r.Options.Name ?? r.Id,
        Description = r.Options.Description,
        Group = r.Options.Group,
        Status = r.Status,
        Schedule = r.Options.Schedule,
        Interval = r.Options.Interval,
        MaxRetries = r.Options.MaxRetries,
        RetryDelay = r.Options.RetryDelay,
        RetryStrategy = r.Options.RetryStrategy,
        Timeout = r.Options.Timeout,
        OverlapPolicy = r.Options.OverlapPolicy,
        CurrentExecutionId = r.CurrentExecutionId,
        NextRunAt = r.NextRunAt,
        LastRunAt = r.LastRunAt,
        LastRunStatus = r.LastRunStatus,
        LastRunDuration = r.LastRunDuration,
    };
}
