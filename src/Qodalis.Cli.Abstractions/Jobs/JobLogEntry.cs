namespace Qodalis.Cli.Abstractions.Jobs;

public class JobLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public required string Level { get; set; }
    public required string Message { get; set; }
}
