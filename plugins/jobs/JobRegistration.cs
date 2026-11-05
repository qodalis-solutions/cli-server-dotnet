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
    /// <summary>Identifier of the currently running execution, or null if idle.</summary>
    public string? CurrentExecutionId { get; internal set; }
    /// <summary>Cancellation token source for the currently running execution.</summary>
    public CancellationTokenSource? CurrentCancellation { get; internal set; }
    /// <summary>The next scheduled run time in UTC.</summary>
    public DateTime? NextRunAt { get; internal set; }
    /// <summary>The timestamp of the last completed run in UTC.</summary>
    public DateTime? LastRunAt { get; internal set; }
    /// <summary>Status string of the last completed run (e.g., "completed", "failed").</summary>
    public string? LastRunStatus { get; internal set; }
    /// <summary>Duration of the last completed run in milliseconds.</summary>
    public long? LastRunDuration { get; internal set; }
    /// <summary>The active scheduling timer, if any.</summary>
    public Timer? Timer { get; internal set; }
}
