namespace Qodalis.Cli.Abstractions.Jobs;

public class CliJobOptions
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Group { get; set; }
    public string? Schedule { get; set; }
    public TimeSpan? Interval { get; set; }
    public bool Enabled { get; set; } = true;
    public int MaxRetries { get; set; } = 0;
    public TimeSpan? Timeout { get; set; }
    public JobOverlapPolicy OverlapPolicy { get; set; } = JobOverlapPolicy.Skip;
}

public enum JobOverlapPolicy
{
    Skip,
    Queue,
    Cancel,
}
