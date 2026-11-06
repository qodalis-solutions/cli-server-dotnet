using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// Represents a registered job with its runtime state (status, scheduling, and execution tracking). Read-only outside
/// the scheduler: act on a job through <see cref="ICliJobScheduler"/>.
/// </summary>
public class JobRegistration
{
    /// <summary>Unique identifier assigned at registration time.</summary>
    public required string Id { get; init; }
    /// <summary>The job implementation instance.</summary>
    public required ICliJob Job { get; init; }
    /// <summary>Configuration options for this job (schedule, retries, timeout, etc.).</summary>
    public required CliJobOptions Options { get; init; }
    /// <summary>Current lifecycle status of the job.</summary>
    public JobStatus Status { get; internal set; } = JobStatus.Active;

    /// <summary>Identifier of the newest executing attempt, or null if none is executing.</summary>
    public string? CurrentExecutionId
    {
        get
        {
            lock (Gate) return Runs.LastOrDefault(r => r.ExecutionId != null)?.ExecutionId;
        }
    }

    /// <summary>
    /// The attempts executing now, oldest first; empty when idle. A run waiting for its retry keeps its slot but has no
    /// entry.
    /// </summary>
    public IReadOnlyList<string> RunningExecutionIds
    {
        get
        {
            lock (Gate) return [.. Runs.Where(r => r.ExecutionId != null).Select(r => r.ExecutionId!)];
        }
    }

    /// <summary>Whether a run waits for a free slot (<see cref="JobOverlapPolicy.Queue"/>).</summary>
    public bool Queued
    {
        get
        {
            lock (Gate) return Pending;
        }
    }

    /// <summary>Cancellation token source of the newest running run.</summary>
    public CancellationTokenSource? CurrentCancellation
    {
        get
        {
            lock (Gate) return Runs.Count == 0 ? null : Runs[^1].Cancellation;
        }
    }

    /// <summary>The next scheduled run time in UTC.</summary>
    public DateTime? NextRunAt { get; internal set; }
    /// <summary>The timestamp of the last completed run in UTC.</summary>
    public DateTime? LastRunAt { get; internal set; }
    /// <summary>Status string of the last completed run (e.g., "completed", "failed").</summary>
    public string? LastRunStatus { get; internal set; }
    /// <summary>Duration of the last completed run in milliseconds.</summary>
    public long? LastRunDuration { get; internal set; }
    /// <summary>The active scheduling timer, if any.</summary>
    public ITimer? Timer { get; internal set; }

    /// <summary>Guards <see cref="Runs"/> and <see cref="Pending"/>: taking a slot is atomic.</summary>
    internal object Gate { get; } = new();

    /// <summary>The runs holding a slot, oldest first (a run holds it across its retries and their delays).</summary>
    internal List<JobRun> Runs { get; } = [];

    /// <summary>A queued run waits for a slot; several due runs while full coalesce into this one.</summary>
    internal bool Pending { get; set; }

    /// <summary>Bumped whenever the timer is replaced or stopped, so a callback of an old timer does nothing.</summary>
    internal int TimerGeneration;
}

/// <summary>One run of a job: its first attempt and its retries, with one cancellation for all of them.</summary>
internal sealed class JobRun
{
    /// <summary>The execution of the attempt executing now; null before the first and between attempts.</summary>
    public string? ExecutionId { get; set; }

    /// <summary>Cancels the run: its running attempt or its retry delay.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>The run's task, done once the run released its slot.</summary>
    public Task Task { get; set; } = Task.CompletedTask;
}
