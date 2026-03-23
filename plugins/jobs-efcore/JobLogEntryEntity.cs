namespace Qodalis.Cli.Plugin.Jobs.EfCore;

/// <summary>
/// EF Core entity representing a single log entry within a job execution.
/// </summary>
internal class JobLogEntryEntity
{
    /// <summary>Auto-incremented primary key.</summary>
    public int Id { get; set; }
    /// <summary>Foreign key to the parent execution.</summary>
    public required string ExecutionId { get; set; }
    /// <summary>UTC timestamp when the log entry was created.</summary>
    public DateTime Timestamp { get; set; }
    /// <summary>Log severity level (e.g., "debug", "info", "warning", "error").</summary>
    public required string Level { get; set; }
    /// <summary>The log message text.</summary>
    public required string Message { get; set; }
    /// <summary>Navigation property to the parent execution entity.</summary>
    public JobExecutionEntity Execution { get; set; } = null!;
}
