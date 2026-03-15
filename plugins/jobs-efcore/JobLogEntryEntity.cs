namespace Qodalis.Cli.Plugin.Jobs.EfCore;

internal class JobLogEntryEntity
{
    public int Id { get; set; }
    public required string ExecutionId { get; set; }
    public DateTime Timestamp { get; set; }
    public required string Level { get; set; }
    public required string Message { get; set; }

    public JobExecutionEntity Execution { get; set; } = null!;
}
