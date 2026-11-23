using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Plugin.Migrations.EfCore;

namespace Qodalis.Cli.Plugin.Jobs.EfCore;

/// <summary>
/// Entity Framework Core implementation of <see cref="ICliJobStorageProvider"/> that persists
/// job executions and state to a relational database.
/// </summary>
public class EfCoreJobStorageProvider : ICliJobStorageProvider
{
    private readonly IDbContextFactory<JobStorageDbContext> _dbFactory;
    private readonly IReadOnlyDictionary<string, string> _renames;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="EfCoreJobStorageProvider"/> class.
    /// </summary>
    /// <param name="dbFactory">The DbContext factory for creating database contexts.</param>
    /// <param name="renames">The previous release's tables to rename on first use (registered by <c>AddEfCoreJobStorage</c>).</param>
    public EfCoreJobStorageProvider(IDbContextFactory<JobStorageDbContext> dbFactory, QcliEfStoreRenames<JobStorageDbContext>? renames = null)
    {
        _dbFactory = dbFactory;
        _renames = renames?.Renames ?? new Dictionary<string, string>();
    }

    private async Task<JobStorageDbContext> CreateDbContextAsync(CancellationToken cancellationToken)
    {
        var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (!_initialized)
        {
            // Once per store, even when the first calls come together (jobs at startup, a burst of sign-ins)
            await _initLock.WaitAsync(cancellationToken);
            try
            {
                if (!_initialized)
                {
                    await EnsureTablesAsync(db, cancellationToken);
                    _initialized = true;
                }
            }
            finally
            {
                _initLock.Release();
            }
        }
        return db;
    }

    /// <summary>
    /// Renames the tables of 1.0.0-beta.29 and older (<c>job_executions</c>, ...) to their new names, keeping the runs,
    /// then creates the database with the tables, or only the tables in a database that already exists (the host's
    /// own, whose other tables make <c>EnsureCreated</c> do nothing). Tables the host created in its migrations are kept.
    /// </summary>
    private async Task EnsureTablesAsync(JobStorageDbContext db, CancellationToken cancellationToken)
    {
        await QcliEfStoreSchema.EnsureAsync(db, _renames, cancellationToken);
        await AddMissingColumnsAsync(db, cancellationToken);
    }

    /// <summary>
    /// Brings tables created by an earlier version up to date: the executions' <c>Properties</c> (1.0.0-beta.15).
    /// </summary>
    private static async Task AddMissingColumnsAsync(JobStorageDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            // Take(1), not FirstOrDefault: EF warns about First without OrderBy on every start, and any row will do.
            _ = await db.Executions.Select(e => e.Properties).Take(1).ToListAsync(cancellationToken);
        }
        catch (DbException)
        {
            var entity = db.Model.FindEntityType(typeof(JobExecutionEntity))!;
            var column = entity.FindProperty(nameof(JobExecutionEntity.Properties))!;
            var sql = db.GetService<ISqlGenerationHelper>();
            var table = sql.DelimitIdentifier(entity.GetTableName()!, entity.GetSchema());
            var name = sql.DelimitIdentifier(column.GetColumnName());
            // Identifiers from the model, delimited by the provider: no user input reaches this statement
            var alter = $"ALTER TABLE {table} ADD {name} {column.GetColumnType()} NULL";
            await db.Database.ExecuteSqlRawAsync(alter, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<int> PruneAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        var cutoff = before.UtcDateTime;
        await using var db = await CreateDbContextAsync(cancellationToken);
        var old = db.Executions.Where(e => e.StartedAt < cutoff);

        _ = await db.LogEntries.Where(l => old.Any(e => e.ExecutionId == l.ExecutionId)).ExecuteDeleteAsync(cancellationToken);
        return await old.ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Set-based: small reads (the newest run, and the run at each count limit) and a delete per limit, never
    /// loading the runs. The count limit deletes the runs older than the job's N-th newest run (ties by row id), the
    /// same as "not among the newest N" and portable to databases without <c>LIMIT</c> in subqueries.
    /// </remarks>
    public async Task<int> PruneAsync(string jobId, JobHistoryRetention retention, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!retention.HasLimits) return 0;

        await using var db = await CreateDbContextAsync(cancellationToken);
        var runs = db.Executions.Where(e => e.JobId == jobId);
        var newestFirst = runs.OrderByDescending(e => e.StartedAt).ThenByDescending(e => e.Id);
        var newest = await newestFirst.Select(e => (int?)e.Id).FirstOrDefaultAsync(cancellationToken);
        if (newest == null) return 0;

        var running = nameof(JobExecutionStatus.Running);
        var failed = nameof(JobExecutionStatus.Failed);
        var deletable = runs.Where(e => e.Status != running && e.Id != newest);
        var deleted = 0;

        // Failed runs, then the others, each with their own limits
        foreach (var isFailed in new[] { true, false })
        {
            var status = isFailed ? JobExecutionStatus.Failed : JobExecutionStatus.Completed;
            var group = isFailed ? deletable.Where(e => e.Status == failed) : deletable.Where(e => e.Status != failed);

            if (retention.MaxAgeFor(status) is { } maxAge)
            {
                var cutoff = now.UtcDateTime - maxAge;
                deleted += await DeleteWithLogsAsync(db, group.Where(e => e.StartedAt < cutoff), cancellationToken);
            }

            if (retention.MaxRunsFor(status) is { } maxRuns)
            {
                // Ranked among the failed runs or the others when failed runs count on their own, else among all
                // runs; running runs and the newest run take their place. The oldest run that is still kept is the
                // boundary: every run after it in the order goes.
                var pool = !retention.CountsFailedSeparately ? runs
                    : isFailed ? runs.Where(e => e.Status == failed) : runs.Where(e => e.Status != failed);
                var boundary = await pool.OrderByDescending(e => e.StartedAt).ThenByDescending(e => e.Id)
                    .Skip(maxRuns - 1)
                    .Select(e => new { e.StartedAt, e.Id })
                    .FirstOrDefaultAsync(cancellationToken);
                if (boundary != null)
                {
                    var older = group.Where(e => e.StartedAt < boundary.StartedAt
                        || (e.StartedAt == boundary.StartedAt && e.Id < boundary.Id));
                    deleted += await DeleteWithLogsAsync(db, older, cancellationToken);
                }
            }
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetExecutionJobIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await CreateDbContextAsync(cancellationToken);
        return await db.Executions.Select(e => e.JobId).Distinct().ToListAsync(cancellationToken);
    }

    private static async Task<int> DeleteWithLogsAsync(JobStorageDbContext db, IQueryable<JobExecutionEntity> executions,
        CancellationToken cancellationToken)
    {
        _ = await db.LogEntries.Where(l => executions.Any(e => e.ExecutionId == l.ExecutionId))
            .ExecuteDeleteAsync(cancellationToken);
        return await executions.ExecuteDeleteAsync(cancellationToken);
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
            entity.Properties = SerializeProperties(execution.Properties);

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
                Properties = SerializeProperties(execution.Properties),
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
            .ThenByDescending(e => e.Id)
            .Skip(offset)
            .Take(limit)
            .Include(e => e.Logs)
            .ToListAsync(cancellationToken);

        var items = entities.Select(ToJobExecution).ToList();
        return (items, total);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<JobExecution> Items, int Total)> QueryExecutionsAsync(
        JobExecutionQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await CreateDbContextAsync(cancellationToken);

        var matches = db.Executions.AsQueryable();
        if (query.JobIds != null)
        {
            var jobIds = query.JobIds.ToList();
            matches = matches.Where(e => jobIds.Contains(e.JobId));
        }
        if (query.Status != null && Enum.TryParse<JobExecutionStatus>(query.Status, true, out var status))
        {
            var statusStr = status.ToString();
            matches = matches.Where(e => e.Status == statusStr);
        }
        if (query.Since is { } since)
        {
            var utc = JobExecutionQuery.AsUtc(since);
            matches = matches.Where(e => e.StartedAt >= utc);
        }

        var total = await matches.CountAsync(cancellationToken);
        var entities = await matches
            .OrderByDescending(e => e.StartedAt)
            .ThenByDescending(e => e.Id)
            .Skip(query.Offset)
            .Take(query.Limit)
            .Include(e => e.Logs)
            .ToListAsync(cancellationToken);

        return (entities.Select(ToJobExecution).ToList(), total);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteExecutionAsync(string executionId, CancellationToken cancellationToken = default)
    {
        await using var db = await CreateDbContextAsync(cancellationToken);
        var entity = await db.Executions.Include(e => e.Logs)
            .FirstOrDefaultAsync(e => e.ExecutionId == executionId, cancellationToken);
        if (entity == null)
            return false;

        db.LogEntries.RemoveRange(entity.Logs);
        db.Executions.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
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

    private static string? SerializeProperties(Dictionary<string, object?> properties) =>
        properties.Count == 0 ? null : JsonSerializer.Serialize(properties);

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
            Properties = entity.Properties == null ? []
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.Properties) ?? [],
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
