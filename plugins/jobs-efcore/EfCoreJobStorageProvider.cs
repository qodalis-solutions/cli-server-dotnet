using Microsoft.EntityFrameworkCore;
using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs.EfCore;

/// <summary>
/// Entity Framework Core implementation of <see cref="ICliJobStorageProvider"/> that persists
/// job executions and state to a relational database.
/// </summary>
public class EfCoreJobStorageProvider : ICliJobStorageProvider
{
    private readonly IDbContextFactory<JobStorageDbContext> _dbFactory;
    private bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="EfCoreJobStorageProvider"/> class.
    /// </summary>
    /// <param name="dbFactory">The DbContext factory for creating database contexts.</param>
    public EfCoreJobStorageProvider(IDbContextFactory<JobStorageDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    private async Task<JobStorageDbContext> CreateDbContextAsync(CancellationToken cancellationToken)
    {
        var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (!_initialized)
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
            _initialized = true;
        }
        return db;
    }

    /// <inheritdoc />
    public async Task SaveExecutionAsync(JobExecution execution, CancellationToken cancellationToken = default)
    {
        await using var db = await CreateDbContextAsync(cancellationToken);

        var entity = await db.Executions
            .FirstOrDefaultAsync(e => e.ExecutionId == execution.Id, cancellationToken);

        if (entity != null)
        {
            entity.Status = execution.Status.ToString();
            entity.CompletedAt = execution.CompletedAt;
            entity.Duration = execution.Duration;
            entity.Error = execution.Error;
            entity.RetryAttempt = execution.RetryAttempt;

            var existingCount = await db.LogEntries
                .CountAsync(l => l.ExecutionId == execution.Id, cancellationToken);

            if (execution.Logs.Count > existingCount)
            {
                var newLogs = execution.Logs.Skip(existingCount).Select(log => new JobLogEntryEntity
                {
                    ExecutionId = execution.Id,
                    Timestamp = log.Timestamp,
                    Level = log.Level,
                    Message = log.Message,
                });
                db.LogEntries.AddRange(newLogs);
            }
        }
        else
        {
            entity = new JobExecutionEntity
            {
                ExecutionId = execution.Id,
                JobId = execution.JobId,
                JobName = execution.JobName,
                Status = execution.Status.ToString(),
                StartedAt = execution.StartedAt,
                CompletedAt = execution.CompletedAt,
                Duration = execution.Duration,
                Error = execution.Error,
                RetryAttempt = execution.RetryAttempt,
                Logs = execution.Logs.Select(log => new JobLogEntryEntity
                {
                    ExecutionId = execution.Id,
                    Timestamp = log.Timestamp,
                    Level = log.Level,
                    Message = log.Message,
                }).ToList(),
            };
            db.Executions.Add(entity);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<(List<JobExecution> Items, int Total)> GetExecutionsAsync(
        string jobId, int limit = 20, int offset = 0, string? statusFilter = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await CreateDbContextAsync(cancellationToken);

        var query = db.Executions.Where(e => e.JobId == jobId);

        if (statusFilter != null && Enum.TryParse<JobExecutionStatus>(statusFilter, true, out var status))
        {
            var statusStr = status.ToString();
            query = query.Where(e => e.Status == statusStr);
        }

        var total = await query.CountAsync(cancellationToken);

        var entities = await query
            .OrderByDescending(e => e.StartedAt)
            .Skip(offset)
            .Take(limit)
            .Include(e => e.Logs)
            .ToListAsync(cancellationToken);

        var items = entities.Select(ToJobExecution).ToList();
        return (items, total);
    }

    /// <inheritdoc />
    public async Task<JobExecution?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default)
    {
        await using var db = await CreateDbContextAsync(cancellationToken);

        var entity = await db.Executions
            .Include(e => e.Logs)
            .FirstOrDefaultAsync(e => e.ExecutionId == executionId, cancellationToken);

        return entity != null ? ToJobExecution(entity) : null;
    }

    /// <inheritdoc />
    public async Task SaveJobStateAsync(string jobId, JobState state, CancellationToken cancellationToken = default)
    {
        await using var db = await CreateDbContextAsync(cancellationToken);

        var entity = await db.States
            .FirstOrDefaultAsync(s => s.JobId == jobId, cancellationToken);

        if (entity != null)
        {
            entity.Status = state.Status.ToString();
            entity.LastRunAt = state.LastRunAt;
            entity.UpdatedAt = state.UpdatedAt;
        }
        else
        {
            entity = new JobStateEntity
            {
                JobId = jobId,
                Status = state.Status.ToString(),
                LastRunAt = state.LastRunAt,
                UpdatedAt = state.UpdatedAt,
            };
            db.States.Add(entity);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<JobState?> GetJobStateAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await CreateDbContextAsync(cancellationToken);

        var entity = await db.States
            .FirstOrDefaultAsync(s => s.JobId == jobId, cancellationToken);

        return entity != null ? ToJobState(entity) : null;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, JobState>> GetAllJobStatesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await CreateDbContextAsync(cancellationToken);

        var entities = await db.States.ToListAsync(cancellationToken);
        return entities.ToDictionary(e => e.JobId, ToJobState);
    }

    private static JobExecution ToJobExecution(JobExecutionEntity entity)
    {
        return new JobExecution
        {
            Id = entity.ExecutionId,
            JobId = entity.JobId,
            JobName = entity.JobName,
            Status = Enum.Parse<JobExecutionStatus>(entity.Status),
            StartedAt = entity.StartedAt,
            CompletedAt = entity.CompletedAt,
            Duration = entity.Duration,
            Error = entity.Error,
            RetryAttempt = entity.RetryAttempt,
            Logs = entity.Logs.OrderBy(l => l.Timestamp).Select(l => new JobLogEntry
            {
                Timestamp = l.Timestamp,
                Level = l.Level,
                Message = l.Message,
            }).ToList(),
        };
    }

    private static JobState ToJobState(JobStateEntity entity)
    {
        return new JobState
        {
            Status = Enum.Parse<JobStatus>(entity.Status),
            LastRunAt = entity.LastRunAt,
            UpdatedAt = entity.UpdatedAt,
        };
    }
}
