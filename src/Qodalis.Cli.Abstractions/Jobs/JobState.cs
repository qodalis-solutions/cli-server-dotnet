namespace Qodalis.Cli.Abstractions.Jobs;

public class JobState
{
    public JobStatus Status { get; set; } = JobStatus.Active;
    public DateTime? LastRunAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum JobStatus
{
    Active,
    Paused,
    Stopped,
}
