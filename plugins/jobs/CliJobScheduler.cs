using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Jobs;

public class CliJobScheduler : IHostedService, IDisposable
{
    private readonly Dictionary<string, JobRegistration> _jobs = new();
    private readonly ICliJobStorageProvider _storage;
    private readonly CliEventSocketManager _eventSocket;
    private readonly ILogger<CliJobScheduler> _logger;

    public CliJobScheduler(
        ICliJobStorageProvider storage,
        CliEventSocketManager eventSocket,
        ILogger<CliJobScheduler> logger)
    {
        _storage = storage;
        _eventSocket = eventSocket;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, JobRegistration> Jobs => _jobs;

    internal void Register(ICliJob job, CliJobOptions options)
    {
        var name = options.Name ?? job.GetType().Name;
        options.Name ??= name;
        options.Description ??= name;

        var id = Guid.NewGuid().ToString("N")[..12];
        var registration = new JobRegistration
        {
            Id = id,
            Job = job,
            Options = options,
            Status = options.Enabled ? JobStatus.Active : JobStatus.Stopped,
        };
        _jobs[id] = registration;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var states = await _storage.GetAllJobStatesAsync(cancellationToken);

        foreach (var (id, reg) in _jobs)
        {
            // Restore persisted state if available
            if (states.TryGetValue(id, out var state))
            {
                reg.Status = state.Status;
                reg.LastRunAt = state.LastRunAt;
            }

            if (reg.Status == JobStatus.Active)
            {
                StartTimer(reg);
            }
        }

        _logger.LogInformation("Job scheduler started with {Count} jobs", _jobs.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var reg in _jobs.Values)
        {
            reg.Timer?.Dispose();
            reg.Timer = null;

            if (reg.CurrentCancellation != null)
            {
                await reg.CurrentCancellation.CancelAsync();
                reg.CurrentCancellation = null;
            }

            await _storage.SaveJobStateAsync(reg.Id, new JobState
            {
                Status = reg.Status,
                LastRunAt = reg.LastRunAt,
                UpdatedAt = DateTime.UtcNow,
            }, cancellationToken);
        }

        _logger.LogInformation("Job scheduler stopped");
    }

    public async Task TriggerAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var reg = GetRegistration(jobId);
        if (reg.CurrentExecutionId != null)
        {
            if (reg.Options.OverlapPolicy == JobOverlapPolicy.Skip)
                throw new InvalidOperationException("Job is already running");
            if (reg.Options.OverlapPolicy == JobOverlapPolicy.Cancel)
                await CancelCurrentAsync(jobId, cancellationToken);
        }
        _ = ExecuteJobAsync(reg);
    }

    public async Task PauseAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var reg = GetRegistration(jobId);
        if (reg.Status == JobStatus.Paused)
            throw new InvalidOperationException("Job is already paused");

        reg.Status = JobStatus.Paused;
        reg.Timer?.Dispose();
        reg.Timer = null;

        await PersistState(reg, cancellationToken);
        await BroadcastAsync("job:paused", new { jobId });
    }

    public async Task ResumeAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var reg = GetRegistration(jobId);
        if (reg.Status != JobStatus.Paused)
            throw new InvalidOperationException("Job is not paused");

        reg.Status = JobStatus.Active;
        StartTimer(reg);

        await PersistState(reg, cancellationToken);
        await BroadcastAsync("job:resumed", new { jobId });
    }

    public async Task StopJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var reg = GetRegistration(jobId);
        reg.Status = JobStatus.Stopped;
        reg.Timer?.Dispose();
        reg.Timer = null;

        if (reg.CurrentCancellation != null)
        {
            await reg.CurrentCancellation.CancelAsync();
            reg.CurrentCancellation = null;
        }

        await PersistState(reg, cancellationToken);
        await BroadcastAsync("job:stopped", new { jobId });
    }

    public async Task CancelCurrentAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var reg = GetRegistration(jobId);
        if (reg.CurrentCancellation == null)
            throw new InvalidOperationException("Job is not currently running");

        await reg.CurrentCancellation.CancelAsync();
        reg.CurrentCancellation = null;
    }

    public Task UpdateOptionsAsync(string jobId, Action<CliJobOptions> update, CancellationToken cancellationToken = default)
    {
        var reg = GetRegistration(jobId);
        update(reg.Options);

        // Restart timer if schedule changed and job is active
        if (reg.Status == JobStatus.Active)
        {
            reg.Timer?.Dispose();
            StartTimer(reg);
        }

        return Task.CompletedTask;
    }

    private void StartTimer(JobRegistration reg)
    {
        if (reg.Options.Schedule != null)
        {
            var cron = CronExpression.Parse(reg.Options.Schedule);
            ScheduleNextCron(reg, cron);
        }
        else if (reg.Options.Interval != null)
        {
            var interval = reg.Options.Interval.Value;
            reg.NextRunAt = DateTime.UtcNow.Add(interval);
            reg.Timer = new Timer(
                _ => OnTimerFired(reg),
                null,
                interval,
                interval);
        }
    }

    private void ScheduleNextCron(JobRegistration reg, CronExpression cron)
    {
        var next = cron.GetNextOccurrence(DateTime.UtcNow);
        if (next == null) return;

        reg.NextRunAt = next.Value;
        var delay = next.Value - DateTime.UtcNow;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        reg.Timer = new Timer(
            _ =>
            {
                OnTimerFired(reg);
                ScheduleNextCron(reg, cron);
            },
            null,
            delay,
            Timeout.InfiniteTimeSpan);
    }

    private void OnTimerFired(JobRegistration reg)
    {
        if (reg.Status != JobStatus.Active) return;

        if (reg.CurrentExecutionId != null)
        {
            switch (reg.Options.OverlapPolicy)
            {
                case JobOverlapPolicy.Skip:
                    return;
                case JobOverlapPolicy.Cancel:
                    reg.CurrentCancellation?.Cancel();
                    break;
                case JobOverlapPolicy.Queue:
                    // Simple queue: fire and forget, the execution will serialize naturally
                    break;
            }
        }

        _ = ExecuteJobAsync(reg);
    }

    private async Task ExecuteJobAsync(JobRegistration reg, int retryAttempt = 0)
    {
        var execution = new JobExecution
        {
            JobId = reg.Id,
            JobName = reg.Options.Name!,
            RetryAttempt = retryAttempt,
        };

        reg.CurrentExecutionId = execution.Id;
        var cts = new CancellationTokenSource();
        reg.CurrentCancellation = cts;

        if (reg.Options.Timeout.HasValue)
        {
            cts.CancelAfter(reg.Options.Timeout.Value);
        }

        var logger = new CliJobLogger();
        var context = new CliJobExecutionContext(logger);

        await _storage.SaveExecutionAsync(execution);
        await BroadcastAsync("job:started", new { jobId = reg.Id, executionId = execution.Id, timestamp = DateTime.UtcNow });

        try
        {
            await reg.Job.ExecuteAsync(context, cts.Token);

            execution.Status = JobExecutionStatus.Completed;
            await BroadcastAsync("job:completed", new { jobId = reg.Id, executionId = execution.Id, duration = execution.Duration });
        }
        catch (OperationCanceledException)
        {
            execution.Status = reg.Options.Timeout.HasValue && cts.IsCancellationRequested
                ? JobExecutionStatus.TimedOut
                : JobExecutionStatus.Cancelled;

            var eventType = execution.Status == JobExecutionStatus.TimedOut ? "job:timed_out" : "job:cancelled";
            await BroadcastAsync(eventType, new { jobId = reg.Id, executionId = execution.Id });
        }
        catch (Exception ex)
        {
            execution.Status = JobExecutionStatus.Failed;
            execution.Error = ex.Message;
            await BroadcastAsync("job:failed", new { jobId = reg.Id, executionId = execution.Id, error = ex.Message });
        }
        finally
        {
            execution.CompletedAt = DateTime.UtcNow;
            execution.Duration = (long)(execution.CompletedAt.Value - execution.StartedAt).TotalMilliseconds;
            execution.Logs = logger.Entries.ToList();
            reg.CurrentExecutionId = null;
            reg.CurrentCancellation = null;
            reg.LastRunAt = execution.StartedAt;
            reg.LastRunStatus = execution.Status.ToString().ToLowerInvariant();
            reg.LastRunDuration = execution.Duration;

            await _storage.SaveExecutionAsync(execution);
            await PersistState(reg);
        }

        // Retry on failure
        if (execution.Status == JobExecutionStatus.Failed && retryAttempt < reg.Options.MaxRetries)
        {
            await ExecuteJobAsync(reg, retryAttempt + 1);
        }
    }

    internal JobRegistration GetRegistration(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var reg))
            throw new KeyNotFoundException($"Job '{jobId}' not found");
        return reg;
    }

    private async Task PersistState(JobRegistration reg, CancellationToken ct = default)
    {
        await _storage.SaveJobStateAsync(reg.Id, new JobState
        {
            Status = reg.Status,
            LastRunAt = reg.LastRunAt,
            UpdatedAt = DateTime.UtcNow,
        }, ct);
    }

    private async Task BroadcastAsync(string type, object data)
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(
                new { type, data },
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            await _eventSocket.BroadcastMessageAsync(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast job event {Type}", type);
        }
    }

    public void Dispose()
    {
        foreach (var reg in _jobs.Values)
        {
            reg.Timer?.Dispose();
            reg.CurrentCancellation?.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
