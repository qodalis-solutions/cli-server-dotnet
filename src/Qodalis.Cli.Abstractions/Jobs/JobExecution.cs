namespace Qodalis.Cli.Abstractions.Jobs;

/// <summary>
/// Records the details and outcome of a single job execution.
/// </summary>
public class JobExecution
{
    /// <summary>Gets or sets the unique execution identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Gets or sets the identifier of the job that was executed.</summary>
    public required string JobId { get; set; }

    /// <summary>Gets or sets the display name of the job.</summary>
    public required string JobName { get; set; }

    /// <summary>Gets or sets the current status of this execution.</summary>
    public JobExecutionStatus Status { get; set; } = JobExecutionStatus.Running;

    /// <summary>Gets or sets when the execution started (UTC).</summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when the execution completed (UTC), if finished.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets the execution duration in milliseconds.</summary>
    public long? Duration { get; set; }

    /// <summary>Gets or sets the error message if the execution failed.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the log entries recorded during execution.</summary>
    public List<JobLogEntry> Logs { get; set; } = [];

    /// <summary>Gets or sets the retry attempt number (0 for the initial attempt).</summary>
    public int RetryAttempt { get; set; } = 0;
}

/// <summary>
/// The status of a job execution.
/// </summary>
public enum JobExecutionStatus
{
    /// <summary>The job is currently running.</summary>
    Running,
    /// <summary>The job completed successfully.</summary>
    Completed,
    /// <summary>The job failed with an error.</summary>
    Failed,
    /// <summary>The job was cancelled before completion.</summary>
    Cancelled,
    /// <summary>The job exceeded its timeout and was terminated.</summary>
    TimedOut,
}
