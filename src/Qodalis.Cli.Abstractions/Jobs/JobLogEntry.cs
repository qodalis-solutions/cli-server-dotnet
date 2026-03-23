namespace Qodalis.Cli.Abstractions.Jobs;

/// <summary>
/// A single log entry recorded during a job execution.
/// </summary>
public class JobLogEntry
{
    /// <summary>Gets or sets when this log entry was created (UTC).</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets the log level (e.g., "debug", "info", "warning", "error").</summary>
    public required string Level { get; set; }

    /// <summary>Gets or sets the log message text.</summary>
    public required string Message { get; set; }
}
