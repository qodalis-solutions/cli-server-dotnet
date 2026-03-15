namespace Qodalis.Cli.Abstractions.Jobs;

public class CliJobOptions
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Group { get; set; }
    public string? Schedule { get; set; }
    public TimeSpan? Interval { get; set; }
    public bool Enabled { get; set; } = true;
    public int MaxRetries { get; set; } = 1;
    /// <summary>
    /// Delay before first retry. Default 5 seconds.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>
    /// Retry backoff strategy: Fixed (constant delay), Linear (delay * attempt), Exponential (delay * 2^attempt). Default Exponential.
    /// </summary>
    public JobRetryStrategy RetryStrategy { get; set; } = JobRetryStrategy.Exponential;
    public TimeSpan? Timeout { get; set; }
    public JobOverlapPolicy OverlapPolicy { get; set; } = JobOverlapPolicy.Skip;
}

public enum JobOverlapPolicy
{
    Skip,
    Queue,
    Cancel,
}

public enum JobRetryStrategy
{
    Fixed,
    Linear,
    Exponential,
}
