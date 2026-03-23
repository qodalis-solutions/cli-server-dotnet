namespace Qodalis.Cli.Abstractions.Jobs;

/// <summary>
/// Represents the persisted state of a scheduled job.
/// </summary>
public class JobState
{
    /// <summary>Gets or sets the current scheduling status of the job.</summary>
    public JobStatus Status { get; set; } = JobStatus.Active;

    /// <summary>Gets or sets when the job last ran (UTC).</summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>Gets or sets when this state was last updated (UTC).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The scheduling status of a job.
/// </summary>
public enum JobStatus
{
    /// <summary>The job is active and will run on its configured schedule.</summary>
    Active,
    /// <summary>The job is temporarily paused and will not run until resumed.</summary>
    Paused,
    /// <summary>The job is stopped and will not run.</summary>
    Stopped,
}
