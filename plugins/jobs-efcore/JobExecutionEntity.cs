namespace Qodalis.Cli.Plugin.Jobs.EfCore;

internal class JobExecutionEntity
{
    public int Id { get; set; }
    public required string ExecutionId { get; set; }
    public required string JobId { get; set; }
    public required string JobName { get; set; }
    public required string Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long? Duration { get; set; }
    public string? Error { get; set; }
    public int RetryAttempt { get; set; }

    public List<JobLogEntryEntity> Logs { get; set; } = [];
}
