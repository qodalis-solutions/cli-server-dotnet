using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="ICliJobStorageProvider"/>.
/// Suitable for development and testing; data is lost on application restart. Lists return the executions with
/// their logs (they are in memory already; see <see cref="JobExecutionQuery.IncludeLogs"/>).
/// Like a database store it keeps copies: a run is saved as it was when <see cref="SaveExecutionAsync"/> was called
/// (the scheduler goes on changing its own object until it saves the finished run) and every read returns a copy.
/// </summary>
public class InMemoryJobStorageProvider : ICliJobStorageProvider
{
    private readonly List<JobExecution> _executions = [];
    private readonly Dictionary<string, JobState> _states = new();
    private readonly object _lock = new();

    /// <inheritdoc />
    public Task SaveExecutionAsync(JobExecution execution, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var copy = Copy(execution);
            var existing = _executions.FindIndex(e => e.Id == execution.Id);
            if (existing >= 0)
                _executions[existing] = copy;
            else
                _executions.Add(copy);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<(List<JobExecution> Items, int Total)> GetExecutionsAsync(
        string jobId, int limit = 20, int offset = 0, string? statusFilter = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var query = _executions.Where(e => e.JobId == jobId);
            if (statusFilter != null && Enum.TryParse<JobExecutionStatus>(statusFilter, true, out var status))
                query = query.Where(e => e.Status == status);

            var items = query.OrderByDescending(e => e.StartedAt).ToList();
            var total = items.Count;
            var page = items.Skip(offset).Take(limit).Select(Copy).ToList();
            return Task.FromResult((page, total));
        }
    }

    /// <inheritdoc />
    public Task<(IReadOnlyList<JobExecution> Items, int Total)> QueryExecutionsAsync(
        JobExecutionQuery query, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IEnumerable<JobExecution> matches = _executions;
            if (query.JobIds != null)
                matches = matches.Where(e => query.JobIds.Contains(e.JobId));
            if (query.Status != null && Enum.TryParse<JobExecutionStatus>(query.Status, true, out var status))
                matches = matches.Where(e => e.Status == status);
            if (query.Since != null)
                matches = matches.Where(e => e.StartedAt >= JobExecutionQuery.AsUtc(query.Since.Value));

            var items = matches.OrderByDescending(e => e.StartedAt).ToList();
            IReadOnlyList<JobExecution> page = items.Skip(query.Offset).Take(query.Limit).Select(Copy).ToList();
            return Task.FromResult((page, items.Count));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobExecutionSummary>> SummarizeExecutionsAsync(DateTime since,
        IReadOnlyCollection<string>? jobIds = null, CancellationToken cancellationToken = default)
    {
        var from = JobExecutionQuery.AsUtc(since);
        lock (_lock)
        {
            return Task.FromResult(JobExecutionSummary.Of(_executions
                .Where(e => JobExecutionQuery.AsUtc(e.StartedAt) >= from && (jobIds == null || jobIds.Contains(e.JobId)))));
        }
    }

    /// <inheritdoc />
    public Task<int> PruneAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        var cutoff = before.UtcDateTime;
        lock (_lock)
        {
            return Task.FromResult(_executions.RemoveAll(e => e.StartedAt < cutoff));
        }
    }

    /// <inheritdoc />
    public Task<int> PruneAsync(string jobId, JobHistoryRetention retention, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var runs = _executions.Where(e => e.JobId == jobId).OrderByDescending(e => e.StartedAt).ToList();
            var expired = retention.SelectExpired(runs, e => e.Status, e => e.StartedAt, now).ToHashSet();
            return Task.FromResult(_executions.RemoveAll(expired.Contains));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetExecutionJobIdsAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<string>>(_executions.Select(e => e.JobId).Distinct().ToList());
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteExecutionAsync(string executionId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_executions.RemoveAll(e => e.Id == executionId) > 0);
        }
    }

    /// <inheritdoc />
    public Task<JobExecution?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_executions.FirstOrDefault(e => e.Id == executionId) is { } execution
                ? Copy(execution)
                : null);
        }
    }

    /// <summary>A copy of the run: its own log list and entries and its own properties (their values are shared).</summary>
    private static JobExecution Copy(JobExecution e) => new()
    {
        Id = e.Id,
        JobId = e.JobId,
        JobName = e.JobName,
        Status = e.Status,
        StartedAt = e.StartedAt,
        CompletedAt = e.CompletedAt,
        Duration = e.Duration,
        Error = e.Error,
        Logs = [.. e.Logs.Select(l => new JobLogEntry { Timestamp = l.Timestamp, Level = l.Level, Message = l.Message })],
        LogCount = e.LogCount,
        RetryAttempt = e.RetryAttempt,
        Properties = new Dictionary<string, object?>(e.Properties),
    };

    /// <inheritdoc />
    public Task SaveJobStateAsync(string jobId, JobState state, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _states[jobId] = state;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<JobState?> GetJobStateAsync(string jobId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_states.GetValueOrDefault(jobId));
        }
    }

    /// <inheritdoc />
    public Task<Dictionary<string, JobState>> GetAllJobStatesAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(new Dictionary<string, JobState>(_states));
        }
    }
}
