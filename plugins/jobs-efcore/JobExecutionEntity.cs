namespace Qodalis.Cli.Plugin.Jobs.EfCore;

/// <summary>
/// EF Core entity representing a single job execution record stored in the database.
/// </summary>
internal class JobExecutionEntity
{
    /// <summary>Auto-incremented primary key.</summary>
    public int Id { get; set; }
    /// <summary>Application-level unique execution identifier.</summary>
    public required string ExecutionId { get; set; }
    /// <summary>Identifier of the job that was executed.</summary>
    public required string JobId { get; set; }
    /// <summary>Human-readable name of the job at the time of execution.</summary>
    public required string JobName { get; set; }
    /// <summary>Execution status (e.g., "Running", "Completed", "Failed").</summary>
    public required string Status { get; set; }
    /// <summary>UTC timestamp when the execution started.</summary>
    public DateTime StartedAt { get; set; }
    /// <summary>UTC timestamp when the execution completed, or null if still running.</summary>
    public DateTime? CompletedAt { get; set; }
    /// <summary>Total execution duration in milliseconds.</summary>
    public long? Duration { get; set; }
    /// <summary>Error message if the execution failed.</summary>
    public string? Error { get; set; }
    /// <summary>Zero-based retry attempt number.</summary>
    public int RetryAttempt { get; set; }
    /// <summary>Log entries produced during this execution.</summary>
    public List<JobLogEntryEntity> Logs { get; set; } = [];
}
