using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// Represents a registered job with its runtime state (status, scheduling, and execution tracking).
/// </summary>
public class JobRegistration
{
    /// <summary>Unique identifier assigned at registration time.</summary>
    public required string Id { get; set; }
    /// <summary>The job implementation instance.</summary>
    public required ICliJob Job { get; set; }
    /// <summary>Configuration options for this job (schedule, retries, timeout, etc.).</summary>
    public required CliJobOptions Options { get; set; }
    /// <summary>Current lifecycle status of the job.</summary>
    public JobStatus Status { get; set; } = JobStatus.Active;
    /// <summary>Identifier of the currently running execution, or null if idle.</summary>
    public string? CurrentExecutionId { get; set; }
    /// <summary>Cancellation token source for the currently running execution.</summary>
    public CancellationTokenSource? CurrentCancellation { get; set; }
    /// <summary>The next scheduled run time in UTC.</summary>
    public DateTime? NextRunAt { get; set; }
    /// <summary>The timestamp of the last completed run in UTC.</summary>
    public DateTime? LastRunAt { get; set; }
    /// <summary>Status string of the last completed run (e.g., "completed", "failed").</summary>
    public string? LastRunStatus { get; set; }
    /// <summary>Duration of the last completed run in milliseconds.</summary>
    public long? LastRunDuration { get; set; }
    /// <summary>The active scheduling timer, if any.</summary>
    public Timer? Timer { get; set; }
}
