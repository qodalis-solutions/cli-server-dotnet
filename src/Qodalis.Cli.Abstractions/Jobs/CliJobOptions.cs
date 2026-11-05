namespace Qodalis.Cli.Abstractions.Jobs;

/// <summary>
/// Configuration options for a scheduled CLI job, including scheduling, retries, and overlap behavior.
/// </summary>
public class CliJobOptions
{
    /// <summary>
    /// The job's id in the jobs API, its history and its saved state (pause, last run), e.g. <c>fetch-rates:bnr</c>.
    /// Keep it stable so a restart finds the job's history and state again. Default: the name, with <c>-2</c>,
    /// <c>-3</c>, ... for further jobs of the same name, in registration order.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the unique name of the job.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets a human-readable description of the job.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the group this job belongs to for organizational purposes.</summary>
    public string? Group { get; set; }

    /// <summary>Gets or sets a cron expression for scheduling (alternative to <see cref="Interval"/>).</summary>
    public string? Schedule { get; set; }

    /// <summary>
    /// Gets or sets the interval between job executions. Across restarts the rhythm is kept: the next run is the
    /// last run plus the interval (right away when that has passed).
    /// </summary>
    public TimeSpan? Interval { get; set; }

    /// <summary>
    /// Runs the job once right after the scheduler starts, whatever its schedule (also for jobs that only run on
    /// demand). Not for paused or stopped jobs. Default is <c>false</c>.
    /// </summary>
    public bool RunOnStart { get; set; }

    /// <summary>Gets or sets whether the job is enabled. Default is <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the maximum number of retry attempts on failure. Default is 1.</summary>
    public int MaxRetries { get; set; } = 1;

    /// <summary>
    /// Delay before first retry. Default 5 seconds.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Retry backoff strategy: Fixed (constant delay), Linear (delay * attempt), Exponential (delay * 2^attempt). Default Exponential.
    /// </summary>
    public JobRetryStrategy RetryStrategy { get; set; } = JobRetryStrategy.Exponential;

    /// <summary>Gets or sets the maximum execution time before the job is cancelled.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Gets or sets the behavior when a new execution is triggered while one is already running.</summary>
    public JobOverlapPolicy OverlapPolicy { get; set; } = JobOverlapPolicy.Skip;
}

/// <summary>
/// Defines the behavior when a job execution overlaps with a previous one still running.
/// </summary>
public enum JobOverlapPolicy
{
    /// <summary>Skip the new execution if the previous one is still running.</summary>
    Skip,
    /// <summary>Queue the new execution to run after the current one completes.</summary>
    Queue,
    /// <summary>Cancel the current execution and start the new one.</summary>
    Cancel,
}

/// <summary>
/// Defines the backoff strategy for retrying failed job executions.
/// </summary>
public enum JobRetryStrategy
{
    /// <summary>Constant delay between retries.</summary>
    Fixed,
    /// <summary>Delay increases linearly with each attempt (delay * attempt).</summary>
    Linear,
    /// <summary>Delay doubles with each attempt (delay * 2^attempt).</summary>
    Exponential,
}
