namespace Qodalis.Cli.Abstractions.Jobs;

public class JobExecution
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public required string JobId { get; set; }
    public required string JobName { get; set; }
    public JobExecutionStatus Status { get; set; } = JobExecutionStatus.Running;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public long? Duration { get; set; }
    public string? Error { get; set; }
    public List<JobLogEntry> Logs { get; set; } = [];
    public int RetryAttempt { get; set; } = 0;
}

public enum JobExecutionStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
    TimedOut,
}
