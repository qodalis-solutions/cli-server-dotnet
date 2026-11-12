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

    /// <summary>
    /// Gets or sets free-form labels besides the single <see cref="Group"/> (e.g. <c>country:ro</c>, <c>bank</c>) to
    /// filter jobs by: <c>GET /jobs?tag=...</c> lists the jobs that have every tag given, ignoring case. Rules in
    /// <see cref="CliJobTags"/>: each 1-64 characters without commas, at most 20, duplicates (ignoring case) dropped.
    /// </summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>Gets or sets a cron expression for scheduling (alternative to <see cref="Interval"/>).</summary>
    public string? Schedule { get; set; }

    /// <summary>
    /// Gets or sets the interval between job executions. Across restarts the rhythm is kept: the next run is the
    /// last run plus the interval (right away when that has passed).
    /// </summary>
    public TimeSpan? Interval { get; set; }

    /// <summary>
    /// Runs the job when the scheduler starts unless it ran recently: an interval job when it never ran or its next
    /// run is due (the last run plus the interval has passed), a cron job when it never ran or missed an occurrence
    /// since its last run, a job without a schedule on every start. Not for paused or stopped jobs. Default is
    /// <c>false</c>.
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

    /// <summary>
    /// Gets or sets what happens when a run is due (schedule, trigger, run on start) while
    /// <see cref="MaxConcurrentRuns"/> runs are going.
    /// </summary>
    public JobOverlapPolicy OverlapPolicy { get; set; } = JobOverlapPolicy.Skip;

    /// <summary>
    /// Gets or sets how many runs of the job may go at the same time, at least 1. Default is 1. A run holds its slot
    /// from its start until it ends, including its retries and the delays before them; when every slot is taken,
    /// <see cref="OverlapPolicy"/> decides. Lowering it cancels nothing: runs finish, and new runs start once fewer
    /// are going.
    /// </summary>
    public int MaxConcurrentRuns { get; set; } = 1;
}

/// <summary>
/// Defines the behavior when a job execution overlaps with a previous one still running.
/// </summary>
public enum JobOverlapPolicy
{
    /// <summary>Drop the due run (a manual trigger is refused).</summary>
    Skip,
    /// <summary>
    /// Queue the due run: it starts when a slot frees. At most one run waits per job, so several due runs while
    /// every slot is taken make one more run.
    /// </summary>
    Queue,
    /// <summary>
    /// Cancel the oldest running run; the due run waits (queued) and starts once that run ended, so the limit holds.
    /// </summary>
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
