using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// Hosted service that schedules and executes registered jobs using cron expressions or intervals.
/// Supports pause, resume, stop, manual trigger, cancellation, configurable retry policies and up to
/// <see cref="CliJobOptions.MaxConcurrentRuns"/> runs of a job at a time.
/// </summary>
public class CliJobScheduler : ICliJobScheduler, IHostedService, IDisposable
{
    private readonly Dictionary<string, JobRegistration> _jobs = new();
    private readonly ICliJobStorageProvider _storage;
    private readonly ICliEventBus _events;
    private readonly ILogger<CliJobScheduler> _logger;
    private readonly IServiceScopeFactory? _scopes;
    private readonly TimeProvider _time;
    private volatile bool _stopping;

    /// <summary>The error of a run a previous process left running (finished as cancelled on start).</summary>
    internal const string InterruptedError = "Interrupted: the server stopped during the run";

    /// <summary>
    /// Initializes a new instance of the <see cref="CliJobScheduler"/> class whose runs get a service scope each.
    /// </summary>
    /// <param name="storage">The storage provider for persisting job state and execution history.</param>
    /// <param name="events">The event bus the job events (<c>job:*</c>, audience <c>jobs:read</c>) are published on.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="scopes">Creates a service scope per run (<see cref="ICliJobExecutionContext.Services"/>); null for none.</param>
    public CliJobScheduler(
        ICliJobStorageProvider storage,
        ICliEventBus events,
        ILogger<CliJobScheduler> logger,
        IServiceScopeFactory? scopes = null)
        : this(storage, events, logger, scopes, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CliJobScheduler"/> class with its own clock: due times, cron
    /// occurrences, retry delays and run times come from <paramref name="time"/> (tests pass a controllable one).
    /// </summary>
    /// <param name="storage">The storage provider for persisting job state and execution history.</param>
    /// <param name="events">The event bus the job events (<c>job:*</c>, audience <c>jobs:read</c>) are published on.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="scopes">Creates a service scope per run (<see cref="ICliJobExecutionContext.Services"/>); null for none.</param>
    /// <param name="time">The clock and timers; null for <see cref="TimeProvider.System"/>.</param>
    public CliJobScheduler(
        ICliJobStorageProvider storage,
        ICliEventBus events,
        ILogger<CliJobScheduler> logger,
        IServiceScopeFactory? scopes,
        TimeProvider? time)
    {
        _storage = storage;
        _events = events;
        _logger = logger;
        _scopes = scopes;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliJobScheduler"/> class that broadcasts its events to every socket
    /// of <paramref name="eventSocket"/> (no audience). Kept for compatibility: prefer the <see cref="ICliEventBus"/> overload.
    /// </summary>
    /// <param name="storage">The storage provider for persisting job state and execution history.</param>
    /// <param name="eventSocket">The WebSocket manager for broadcasting job events.</param>
    /// <param name="logger">The logger instance.</param>
    public CliJobScheduler(
        ICliJobStorageProvider storage,
        ICliEventSocketManager eventSocket,
        ILogger<CliJobScheduler> logger)
        : this(storage, eventSocket, logger, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CliJobScheduler"/> class whose runs get a service scope each and
    /// that broadcasts its events to every socket of <paramref name="eventSocket"/> (no audience). Kept for
    /// compatibility: prefer the <see cref="ICliEventBus"/> overload.
    /// </summary>
    /// <param name="storage">The storage provider for persisting job state and execution history.</param>
    /// <param name="eventSocket">The WebSocket manager for broadcasting job events.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="scopes">Creates a service scope per run (<see cref="ICliJobExecutionContext.Services"/>); null for none.</param>
    public CliJobScheduler(
        ICliJobStorageProvider storage,
        ICliEventSocketManager eventSocket,
        ILogger<CliJobScheduler> logger,
        IServiceScopeFactory? scopes)
        : this(storage, BroadcastingBus(eventSocket), logger, scopes)
    {
    }

    private static CliEventBus BroadcastingBus(ICliEventSocketManager eventSocket)
    {
        var bus = new CliEventBus();
        bus.Subscribe("*", (e, _) => eventSocket.BroadcastMessageAsync(CliEventBus.Serialize(e)));
        return bus;
    }

    /// <summary>
    /// Gets all registered job registrations keyed by job ID.
    /// </summary>
    public IReadOnlyDictionary<string, JobRegistration> Jobs => _jobs;

    internal void Register(ICliJob job, CliJobOptions options)
    {
        var name = options.Name ?? job.GetType().Name;
        options.Name ??= name;
        options.Description ??= name;
        if (options.MaxConcurrentRuns < 1)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxConcurrentRuns,
                $"Job '{name}': MaxConcurrentRuns must be at least 1");

        string id;
        if (options.Id != null)
        {
            if (string.IsNullOrWhiteSpace(options.Id) || options.Id.IndexOfAny(['/', '\\', '?', '#']) >= 0)
                throw new ArgumentException($"Job id '{options.Id}' must be non-empty and may not contain '/', '\\', '?' or '#' (it is part of the jobs API's URLs)", nameof(options));
            if (_jobs.ContainsKey(options.Id))
                throw new InvalidOperationException($"A job with id '{options.Id}' is already registered");
            id = options.Id;
        }
        else
        {
            // Stable across restarts, so saved state and history find the job again; URL-safe for the jobs API
            var baseId = string.Concat(name.Select(c => c is '/' or '\\' or '?' or '#' ? '-' : c));
            id = baseId;
            for (var n = 2; _jobs.ContainsKey(id); n++)
                id = $"{baseId}-{n}";
        }
        var registration = new JobRegistration
        {
            Id = id,
            Job = job,
            Options = options,
            Status = options.Enabled ? JobStatus.Active : JobStatus.Stopped,
        };
        _jobs[id] = registration;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = false;
        var states = await _storage.GetAllJobStatesAsync(cancellationToken);
        await FinishInterruptedRunsAsync(cancellationToken);
        var now = Now;

        foreach (var (id, reg) in _jobs)
        {
            // Restore the saved state: the last run, and a pause or stop made at run time. A job the code disables
            // (Enabled = false) stays stopped whatever was saved.
            if (states.TryGetValue(id, out var state))
            {
                reg.LastRunAt = state.LastRunAt is { } last ? JobExecutionQuery.AsUtc(last) : null;
                if (reg.Options.Enabled)
                    reg.Status = state.Status;
            }

            if (reg.Status != JobStatus.Active) continue;

            // An interval job runs on start through its timer (due now), so the two never overlap. One that ran
            // before is due one interval after its last run anyway: the timer keeps that rhythm.
            var isInterval = reg.Options.Schedule == null && reg.Options.Interval != null;
            StartTimer(reg, runNow: isInterval && reg.Options.RunOnStart && reg.LastRunAt == null);

            if (!isInterval && reg.Options.RunOnStart && IsDueOnStart(reg, now))
                RunDue(reg);
        }

        _logger.LogInformation("Job scheduler started with {Count} jobs", _jobs.Count);
    }

    /// <summary>
    /// Whether a cron or manual job with <see cref="CliJobOptions.RunOnStart"/> runs on start: a manual job always, a
    /// cron job when it never ran or missed an occurrence since its last run.
    /// </summary>
    private static bool IsDueOnStart(JobRegistration reg, DateTime now)
    {
        if (reg.Options.Schedule == null || reg.LastRunAt is not { } last) return true;
        var missed = CronExpression.Parse(reg.Options.Schedule).GetNextOccurrence(JobExecutionQuery.AsUtc(last));
        return missed != null && missed.Value <= now;
    }

    /// <summary>
    /// Finishes the runs of the registered jobs that the store still has as running: a previous process stopped during
    /// them. They become cancelled, completed at their start (the stop time is unknown). No events: no client is
    /// connected yet.
    /// </summary>
    private async Task FinishInterruptedRunsAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        foreach (var id in _jobs.Keys)
        {
            try
            {
                var seen = new HashSet<string>();
                while (true)
                {
                    // Finished runs leave the filter, so every page starts at 0; a run the store did not update is
                    // seen again and ends the loop.
                    var (items, _) = await _storage.GetExecutionsAsync(
                        id, pageSize, 0, nameof(JobExecutionStatus.Running), cancellationToken);
                    var interrupted = items.Where(e => e.Status == JobExecutionStatus.Running && seen.Add(e.Id)).ToList();
                    foreach (var execution in interrupted)
                    {
                        execution.Status = JobExecutionStatus.Cancelled;
                        execution.Error = InterruptedError;
                        execution.CompletedAt ??= execution.StartedAt;
                        execution.Duration = Math.Max(0, (long)(execution.CompletedAt.Value - execution.StartedAt).TotalMilliseconds);
                        await _storage.SaveExecutionAsync(execution, cancellationToken);
                    }

                    if (interrupted.Count == 0 || items.Count < pageSize) break;
                }

                if (seen.Count > 0)
                    _logger.LogWarning("Finished {Count} interrupted run(s) of job {JobId} as cancelled", seen.Count, id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Finishing the interrupted runs of job {JobId} failed", id);
            }
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        foreach (var reg in _jobs.Values)
        {
            StopTimer(reg);
            CancelRuns(reg);

            await _storage.SaveJobStateAsync(reg.Id, new JobState
            {
                Status = reg.Status,
                LastRunAt = reg.LastRunAt,
                UpdatedAt = Now,
            }, cancellationToken);
        }

        _logger.LogInformation("Job scheduler stopped");
    }

    /// <summary>
    /// Manually triggers immediate execution of the specified job. When <see cref="CliJobOptions.MaxConcurrentRuns"/>
    /// runs are going, the overlap policy decides: <c>Skip</c> throws <see cref="InvalidOperationException"/>,
    /// <c>Queue</c> queues the run, <c>Cancel</c> cancels the oldest run and starts this one when it ended.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to trigger.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public Task TriggerAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var reg = GetRegistration(jobId);
        if (_stopping)
            throw new InvalidOperationException("The job scheduler is stopping");
        if (RunDue(reg) == DueOutcome.Skipped)
            throw new InvalidOperationException("Job is already running");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pauses the specified job, stopping its scheduled timer.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to pause.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task PauseAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var reg = GetRegistration(jobId);
        if (reg.Status == JobStatus.Paused)
            throw new InvalidOperationException("Job is already paused");

        reg.Status = JobStatus.Paused;
        StopTimer(reg);

        await PersistState(reg, cancellationToken);
        await PublishAsync("job:paused", new { jobId, status = "paused" });
    }

    /// <summary>
    /// Resumes a previously paused job and restarts its scheduled timer.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to resume.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task ResumeAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var reg = GetRegistration(jobId);
        if (reg.Status != JobStatus.Paused)
            throw new InvalidOperationException("Job is not paused");

        reg.Status = JobStatus.Active;
        StartTimer(reg);

        await PersistState(reg, cancellationToken);
        await PublishAsync("job:resumed", new { jobId, status = "active" });
    }

    /// <summary>
    /// Stops the specified job: cancels every running run, drops a queued one and disposes its timer.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to stop.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task StopJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var reg = GetRegistration(jobId);
        reg.Status = JobStatus.Stopped;
        StopTimer(reg);
        CancelRuns(reg);

        await PersistState(reg, cancellationToken);
        await PublishAsync("job:stopped", new { jobId, status = "stopped" });
    }

    /// <summary>
    /// Cancels every running run of the specified job and drops a queued one.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job whose runs to cancel.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public Task CancelCurrentAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var reg = GetRegistration(jobId);
        if (!CancelRuns(reg))
            throw new InvalidOperationException("Job is not currently running");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Updates the options for the specified job and restarts its timer if the schedule changed. A raised
    /// <see cref="CliJobOptions.MaxConcurrentRuns"/> starts a queued run; a lowered one cancels nothing.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to update.</param>
    /// <param name="update">A callback that mutates the job options.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="CliJobOptions.MaxConcurrentRuns"/> below 1 (not applied).</exception>
    public Task UpdateOptionsAsync(string jobId, Action<CliJobOptions> update, CancellationToken cancellationToken = default)
    {
        var reg = GetRegistration(jobId);
        var maxConcurrentRuns = reg.Options.MaxConcurrentRuns;
        update(reg.Options);
        if (reg.Options.MaxConcurrentRuns < 1)
        {
            var invalid = reg.Options.MaxConcurrentRuns;
            reg.Options.MaxConcurrentRuns = maxConcurrentRuns;
            throw new ArgumentOutOfRangeException(nameof(CliJobOptions.MaxConcurrentRuns), invalid,
                "maxConcurrentRuns must be an integer of at least 1");
        }

        // Restart timer if schedule changed and job is active
        if (reg.Status == JobStatus.Active)
        {
            StopTimer(reg);
            StartTimer(reg);
        }

        lock (reg.Gate)
        {
            StartPendingLocked(reg);
        }

        return Task.CompletedTask;
    }

    private void StartTimer(JobRegistration reg, bool runNow = false)
    {
        var generation = reg.TimerGeneration;
        if (reg.Options.Schedule != null)
        {
            var cron = CronExpression.Parse(reg.Options.Schedule);
            ScheduleCron(reg, cron, Now, generation);
        }
        else if (reg.Options.Interval != null)
        {
            var interval = reg.Options.Interval.Value;
            var now = Now;
            // Keep the rhythm across restarts and pauses: due one interval after the last run
            var due = reg.LastRunAt is { } last ? last.Add(interval) : now.Add(interval);
            if (due < now || runNow) due = now;
            reg.NextRunAt = due;
            reg.Timer = _time.CreateTimer(
                _ =>
                {
                    if (generation != Volatile.Read(ref reg.TimerGeneration)) return;
                    reg.NextRunAt = Now.Add(interval);
                    OnTimerFired(reg);
                },
                null,
                due - now,
                interval);
        }
    }

    /// <summary>
    /// Arms the timer for the cron occurrence after <paramref name="after"/> (exclusive). When that is already past
    /// (the process was busy, the clock jumped) the next occurrence after now is used instead.
    /// </summary>
    private void ScheduleCron(JobRegistration reg, CronExpression cron, DateTime after, int generation)
    {
        var now = Now;
        var next = cron.GetNextOccurrence(after);
        if (next != null && next.Value <= now)
            next = cron.GetNextOccurrence(now);
        if (next == null)
        {
            reg.NextRunAt = null;
            return;
        }

        reg.NextRunAt = next.Value;
        var delay = next.Value - now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        var occurrence = next.Value;
        reg.Timer = _time.CreateTimer(
            _ => OnCronFired(reg, cron, occurrence, generation),
            null,
            delay,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// A cron timer fired for <paramref name="occurrence"/>: the next one is computed from it, not from now, so a tick
    /// that fires early never runs the same occurrence twice.
    /// </summary>
    internal void OnCronFired(JobRegistration reg, CronExpression cron, DateTime occurrence, int generation)
    {
        if (generation != Volatile.Read(ref reg.TimerGeneration)) return;
        reg.Timer?.Dispose();
        ScheduleCron(reg, cron, occurrence, generation);
        OnTimerFired(reg);
    }

    /// <summary>Disposes the job's timer; a callback of it already under way does nothing.</summary>
    private static void StopTimer(JobRegistration reg)
    {
        Interlocked.Increment(ref reg.TimerGeneration);
        reg.Timer?.Dispose();
        reg.Timer = null;
    }

    /// <summary>A scheduled run of the job is due.</summary>
    internal void OnTimerFired(JobRegistration reg)
    {
        if (reg.Status != JobStatus.Active) return;
        RunDue(reg);
    }

    internal enum DueOutcome
    {
        Started,
        Queued,
        Skipped,
    }

    /// <summary>
    /// A run of the job is due (timer, trigger, run on start): it takes a free slot, or the overlap policy decides
    /// when <see cref="CliJobOptions.MaxConcurrentRuns"/> runs are going. Atomic per job.
    /// </summary>
    internal DueOutcome RunDue(JobRegistration reg)
    {
        JobRun? cancel = null;
        DueOutcome outcome;
        lock (reg.Gate)
        {
            if (_stopping)
                return DueOutcome.Skipped;

            if (reg.Runs.Count < reg.Options.MaxConcurrentRuns)
            {
                StartRunLocked(reg);
                return DueOutcome.Started;
            }

            switch (reg.Options.OverlapPolicy)
            {
                case JobOverlapPolicy.Queue:
                    // At most one run waits: several due runs while full make one more run, not a backlog
                    reg.Pending = true;
                    outcome = DueOutcome.Queued;
                    break;
                case JobOverlapPolicy.Cancel:
                    // The oldest run makes room; the new run starts when it ended, so the limit holds. A second due
                    // run meanwhile joins the waiting one instead of cancelling another run.
                    if (!reg.Pending)
                    {
                        cancel = reg.Runs.FirstOrDefault(r => !r.CancelRequested);
                        if (cancel != null) cancel.CancelRequested = true;
                    }
                    reg.Pending = true;
                    outcome = DueOutcome.Queued;
                    break;
                default:
                    outcome = DueOutcome.Skipped;
                    break;
            }
        }

        if (cancel != null)
            Cancel(cancel);
        else if (outcome == DueOutcome.Skipped)
            _logger.LogDebug("Job {JobId} is due but {Count} run(s) are going; skipped", reg.Id, reg.Options.MaxConcurrentRuns);
        return outcome;
    }

    /// <summary>Takes a slot and starts a run in the background. Call with <see cref="JobRegistration.Gate"/> held.</summary>
    private void StartRunLocked(JobRegistration reg)
    {
        var run = new JobRun(new JobExecution { JobId = reg.Id, JobName = reg.Options.Name! });
        reg.Runs.Add(run);
        // Off the caller's path (a timer callback, a request, the host's startup): the run may take a while
        run.Task = Task.Run(() => RunAsync(reg, run), CancellationToken.None);
    }

    /// <summary>Starts the waiting run when a slot is free. Call with <see cref="JobRegistration.Gate"/> held.</summary>
    private void StartPendingLocked(JobRegistration reg)
    {
        if (!reg.Pending || _stopping || reg.Runs.Count >= reg.Options.MaxConcurrentRuns) return;
        reg.Pending = false;
        StartRunLocked(reg);
    }

    /// <summary>Cancels every running run of the job and drops a waiting one; false when there was neither.</summary>
    private bool CancelRuns(JobRegistration reg)
    {
        List<JobRun> runs;
        lock (reg.Gate)
        {
            var any = reg.Runs.Count > 0 || reg.Pending;
            reg.Pending = false;
            runs = [.. reg.Runs];
            foreach (var run in runs) run.CancelRequested = true;
            if (!any) return false;
        }

        foreach (var run in runs) Cancel(run);
        return true;
    }

    private void Cancel(JobRun run)
    {
        try
        {
            run.Cancellation.Cancel();
        }
        catch (Exception ex)
        {
            // A job's cancellation callback threw; the run is cancelled all the same
            _logger.LogWarning(ex, "Cancelling run {ExecutionId} raised an error", run.ExecutionId);
        }
    }

    /// <summary>
    /// Runs the job with its retries, holding its slot until the last attempt (and every delay before a retry) ended;
    /// nothing a run throws is left unobserved.
    /// </summary>
    private async Task RunAsync(JobRegistration reg, JobRun run)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var execution = await ExecuteOnceAsync(reg, run, attempt);
                if (execution.Status != JobExecutionStatus.Failed || attempt >= reg.Options.MaxRetries
                    || run.Cancellation.IsCancellationRequested)
                    return;

                var delay = reg.Options.RetryStrategy switch
                {
                    JobRetryStrategy.Fixed => reg.Options.RetryDelay,
                    JobRetryStrategy.Linear => TimeSpan.FromTicks(reg.Options.RetryDelay.Ticks * (attempt + 1)),
                    JobRetryStrategy.Exponential => TimeSpan.FromTicks(reg.Options.RetryDelay.Ticks * (long)Math.Pow(2, attempt)),
                    _ => reg.Options.RetryDelay,
                };
                _logger.LogInformation("Retrying job {JobName} in {Delay} (attempt {Attempt}/{MaxRetries})",
                    reg.Options.Name, delay, attempt + 1, reg.Options.MaxRetries);
                await PublishAsync("job:retrying", new
                {
                    jobId = reg.Id,
                    executionId = execution.Id,
                    attempt = attempt + 1,
                    maxRetries = reg.Options.MaxRetries,
                    delayMs = (long)delay.TotalMilliseconds,
                });
                try
                {
                    await Task.Delay(delay, _time, run.Cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled while waiting for the retry: the run ends with its failed attempt
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Running job {JobId} failed", reg.Id);
        }
        finally
        {
            lock (reg.Gate)
            {
                reg.Runs.Remove(run);
                StartPendingLocked(reg);
            }
        }
    }

    /// <summary>One attempt of a run, in its own service scope (disposed when the attempt ends, before any retry).</summary>
    private async Task<JobExecution> ExecuteOnceAsync(JobRegistration reg, JobRun run, int retryAttempt)
    {
        var execution = retryAttempt == 0
            ? run.First
            : new JobExecution { JobId = reg.Id, JobName = reg.Options.Name! };
        execution.RetryAttempt = retryAttempt;
        execution.StartedAt = Now;
        lock (reg.Gate)
        {
            run.ExecutionId = execution.Id;
        }

        // The run's cancellation (cancel, stop, the Cancel policy) and this attempt's timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(run.Cancellation.Token);
        if (reg.Options.Timeout.HasValue)
        {
            cts.CancelAfter(reg.Options.Timeout.Value);
        }

        var logger = new CliJobLogger();
        await using var scope = _scopes?.CreateAsyncScope();
        var context = new CliJobExecutionContext(logger, scope?.ServiceProvider ?? EmptyServices.Instance);

        await _storage.SaveExecutionAsync(execution);
        await PublishAsync("job:started", new { jobId = reg.Id, executionId = execution.Id });

        // Published once the run is saved, so a client reloading on the event sees the finished run.
        string finishedEvent;
        object? finishedData = null;
        try
        {
            await reg.Job.ExecuteAsync(context, cts.Token);

            execution.Status = JobExecutionStatus.Completed;
            finishedEvent = "job:completed";
        }
        catch (OperationCanceledException)
        {
            execution.Status = cts.IsCancellationRequested && !run.Cancellation.IsCancellationRequested
                ? JobExecutionStatus.TimedOut
                : JobExecutionStatus.Cancelled;

            finishedEvent = execution.Status == JobExecutionStatus.TimedOut ? "job:timed_out" : "job:cancelled";
            finishedData = new { jobId = reg.Id, executionId = execution.Id };
        }
        catch (Exception ex)
        {
            execution.Status = JobExecutionStatus.Failed;
            execution.Error = ex.Message;
            finishedEvent = "job:failed";
            finishedData = new { jobId = reg.Id, executionId = execution.Id, error = ex.Message };
        }
        finally
        {
            execution.CompletedAt = Now;
            execution.Duration = (long)(execution.CompletedAt.Value - execution.StartedAt).TotalMilliseconds;
            execution.Logs = logger.Entries.ToList();
            execution.Properties = new Dictionary<string, object?>(context.Properties);
            reg.LastRunAt = execution.StartedAt;
            reg.LastRunStatus = execution.Status.ToString().ToLowerInvariant();
            reg.LastRunDuration = execution.Duration;

            await SaveFinishedAsync(execution, context.Discarded);
            await TryPersistStateAsync(reg);
        }

        finishedData ??= new { jobId = reg.Id, executionId = execution.Id, duration = execution.Duration };
        await PublishAsync(finishedEvent, finishedData);

        return execution;
    }

    /// <summary>
    /// Saves the finished run (or deletes a discarded one). When the store refuses the run's properties (a value that
    /// does not serialize), the run is saved without them, so it never stays "running".
    /// </summary>
    private async Task SaveFinishedAsync(JobExecution execution, bool discard)
    {
        try
        {
            var discarded = discard && execution.Status == JobExecutionStatus.Completed
                && await _storage.DeleteExecutionAsync(execution.Id);
            if (!discarded)
                await _storage.SaveExecutionAsync(execution);
        }
        catch (Exception ex) when (execution.Properties is { Count: > 0 })
        {
            _logger.LogError(ex, "Saving execution {ExecutionId} of job {JobId} failed; saving it without its properties",
                execution.Id, execution.JobId);
            execution.Properties = [];
            execution.Logs.Add(new JobLogEntry
            {
                Level = "error",
                Message = $"The run's properties could not be saved: {ex.Message}",
            });
            await _storage.SaveExecutionAsync(execution);
        }
    }

    private async Task TryPersistStateAsync(JobRegistration reg)
    {
        try
        {
            await PersistState(reg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the state of job {JobId} failed", reg.Id);
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
            UpdatedAt = Now,
        }, ct);
    }

    /// <summary>Publishes a job event (audience <c>jobs:read</c>); never fails the scheduler.</summary>
    private async Task PublishAsync(string type, object data)
    {
        try
        {
            await _events.PublishAsync(type, data, JobEvents.Audience);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish job event {Type}", type);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var reg in _jobs.Values)
        {
            reg.Timer?.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>A service provider without services, for jobs run without a host.</summary>
internal sealed class EmptyServices : IServiceProvider
{
    public static readonly EmptyServices Instance = new();

    public object? GetService(Type serviceType) => null;
}
