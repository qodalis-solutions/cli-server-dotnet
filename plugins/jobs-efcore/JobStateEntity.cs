namespace Qodalis.Cli.Plugin.Jobs.EfCore;

/// <summary>
/// EF Core entity representing the persisted state of a registered job.
/// </summary>
internal class JobStateEntity
{
    /// <summary>Auto-incremented primary key.</summary>
    public int Id { get; set; }
    /// <summary>Unique job identifier matching the scheduler's registration ID.</summary>
    public required string JobId { get; set; }
    /// <summary>Current job status (e.g., "Active", "Paused", "Stopped").</summary>
    public required string Status { get; set; }
    /// <summary>UTC timestamp of the last execution, or null if never run.</summary>
    public DateTime? LastRunAt { get; set; }
    /// <summary>UTC timestamp when this state record was last updated.</summary>
    public DateTime UpdatedAt { get; set; }
}
