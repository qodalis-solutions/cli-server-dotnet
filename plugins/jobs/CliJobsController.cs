using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Abstractions.Insights;
using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// REST API controller for managing jobs (list, trigger, pause, resume, stop, cancel, update, history), through the
/// registered <see cref="ICliJobManager"/>.
/// </summary>
[ApiController]
[Route("api/v1/qcli/jobs")]
public class CliJobsController : ControllerBase
{
    private readonly ICliJobManager _jobs;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliJobsController"/> class.
    /// </summary>
    /// <param name="jobs">The job manager (by default the in-process scheduler).</param>
    public CliJobsController(ICliJobManager jobs)
    {
        _jobs = jobs;
    }

    /// <summary>
    /// Lists all jobs with their current status and configuration.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListJobs(CancellationToken ct = default)
    {
        var jobs = await _jobs.GetJobsAsync(ct);
        return Ok(jobs.Select(MapJobDto));
    }

    /// <summary>
    /// Gets details of a specific job by ID.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetJob(string id, CancellationToken ct = default)
    {
        var job = await _jobs.GetJobAsync(id, ct);
        return job == null ? JobNotFound() : Ok(MapJobDto(job));
    }

    /// <summary>
    /// Manually triggers a job for immediate execution.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    [HttpPost("{id}/trigger")]
    public Task<IActionResult> TriggerJob(string id, CancellationToken ct) =>
        RunAsync(() => _jobs.TriggerAsync(id, ct), "Job triggered", "JOB_ALREADY_RUNNING");

    /// <summary>
    /// Pauses a job, preventing further scheduled executions.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    [HttpPost("{id}/pause")]
    public Task<IActionResult> PauseJob(string id, CancellationToken ct) =>
        RunAsync(() => _jobs.PauseAsync(id, ct), "Job paused", "JOB_ALREADY_PAUSED");

    /// <summary>
    /// Resumes a previously paused job.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    [HttpPost("{id}/resume")]
    public Task<IActionResult> ResumeJob(string id, CancellationToken ct) =>
        RunAsync(() => _jobs.ResumeAsync(id, ct), "Job resumed", "JOB_NOT_PAUSED");

    /// <summary>
    /// Stops a job entirely, cancelling any running execution.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    [HttpPost("{id}/stop")]
    public Task<IActionResult> StopJob(string id, CancellationToken ct) =>
        RunAsync(() => _jobs.StopAsync(id, ct), "Job stopped", "JOB_CONFLICT");

    /// <summary>
    /// Cancels the currently running execution of a job.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    [HttpPost("{id}/cancel")]
    public Task<IActionResult> CancelJob(string id, CancellationToken ct) =>
        RunAsync(() => _jobs.CancelAsync(id, ct), "Execution cancelled", "JOB_NOT_RUNNING");

    /// <summary>
    /// Updates the configuration of a job (schedule, retries, timeout, etc.).
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="request">The update request body.</param>
    /// <param name="ct">A cancellation token.</param>
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateJob(string id, [FromBody] UpdateJobRequest request, CancellationToken ct)
    {
        if (request.Schedule != null && request.Interval != null)
            return BadRequest(new { error = "Cannot provide both schedule and interval", code = "SCHEDULE_CONFLICT" });

        try
        {
            return await RunAsync(() => _jobs.UpdateAsync(id, options =>
            {
                if (request.Description != null) options.Description = request.Description;
                if (request.Group != null) options.Group = request.Group;
                if (request.Schedule != null) { options.Schedule = request.Schedule; options.Interval = null; }
                if (request.Interval != null) { options.Interval = IntervalParser.Parse(request.Interval); options.Schedule = null; }
                if (request.MaxRetries.HasValue) options.MaxRetries = request.MaxRetries.Value;
                if (request.RetryDelay != null) options.RetryDelay = IntervalParser.Parse(request.RetryDelay);
                if (request.RetryStrategy != null && Enum.TryParse<JobRetryStrategy>(request.RetryStrategy, true, out var strategy))
                    options.RetryStrategy = strategy;
                if (request.Timeout != null) options.Timeout = IntervalParser.Parse(request.Timeout);
                if (request.OverlapPolicy != null && Enum.TryParse<JobOverlapPolicy>(request.OverlapPolicy, true, out var policy))
                    options.OverlapPolicy = policy;
            }, ct), "Job updated", "JOB_CONFLICT");
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message, code = "INVALID_SCHEDULE" });
        }
    }

    /// <summary>
    /// Retrieves paginated execution history for a job, optionally filtered by status.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="limit">Maximum number of entries to return (capped at 100).</param>
    /// <param name="offset">Number of entries to skip for pagination.</param>
    /// <param name="status">Optional status filter (e.g., "completed", "failed").</param>
    /// <param name="ct">A cancellation token.</param>
    [HttpGet("{id}/history")]
    public async Task<IActionResult> GetHistory(
        string id,
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        if (await _jobs.GetJobAsync(id, ct) == null)
            return JobNotFound();

        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(offset, 0);
        var (items, total) = await _jobs.GetExecutionsAsync(id, limit, offset, status, ct);

        return Ok(new
        {
            items = items.Select(e => new
            {
                id = e.Id,
                jobId = e.JobId,
                jobName = e.JobName,
                status = e.Status.ToString().ToLowerInvariant(),
                startedAt = e.StartedAt,
                completedAt = e.CompletedAt,
                duration = e.Duration,
                error = e.Error,
                retryAttempt = e.RetryAttempt,
                logCount = e.Logs.Count,
                properties = e.Properties,
            }),
            total,
            limit,
            offset,
        });
    }

    /// <summary>
    /// Lists executions across jobs, newest first: every job, the jobs named in <paramref name="job"/> (repeat it or
    /// separate ids with commas), or the jobs of a <paramref name="group"/>; optionally only those started
    /// <paramref name="since"/> a time (ISO 8601; without an offset it is UTC).
    /// </summary>
    [HttpGet("executions")]
    public async Task<IActionResult> GetExecutions(
        [FromQuery] string[]? job = null,
        [FromQuery] string? group = null,
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0,
        [FromQuery] string? status = null,
        [FromQuery] DateTime? since = null,
        CancellationToken ct = default)
    {
        var query = new JobExecutionQuery
        {
            JobIds = await ResolveJobIdsAsync(job, group, ct),
            Status = status,
            Since = since is { } s ? JobExecutionQuery.AsUtc(s) : null,
            Limit = Math.Clamp(limit, 1, 100),
            Offset = Math.Max(offset, 0),
        };
        IReadOnlyList<JobExecution> items;
        int total;
        try
        {
            (items, total) = query.JobIds is { Count: 0 } ? ([], 0) : await _jobs.QueryExecutionsAsync(query, ct);
        }
        catch (NotSupportedException ex)
        {
            return StatusCode(501, new { error = ex.Message, code = "NOT_SUPPORTED" });
        }

        return Ok(new
        {
            items = items.Select(e => new
            {
                id = e.Id,
                jobId = e.JobId,
                jobName = e.JobName,
                status = e.Status.ToString().ToLowerInvariant(),
                startedAt = e.StartedAt,
                completedAt = e.CompletedAt,
                duration = e.Duration,
                error = e.Error,
                retryAttempt = e.RetryAttempt,
                logCount = e.Logs.Count,
                properties = e.Properties,
            }),
            total,
            limit = query.Limit,
            offset = query.Offset,
        });
    }

    /// <summary>
    /// Statistics of the runs started in <c>[since, until)</c> (ISO 8601; default the last 24 hours): outcomes per
    /// column (<paramref name="buckets"/>, default 24, 1..200), totals and per-job figures. <paramref name="job"/> and
    /// <paramref name="group"/> narrow the jobs as in <see cref="GetExecutions"/>. Reads every run in the range through
    /// <see cref="ICliJobManager.QueryExecutionsAsync"/>, so it works with any job store.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(
        [FromQuery] string? since = null,
        [FromQuery] string? until = null,
        [FromQuery] string? buckets = null,
        [FromQuery] string[]? job = null,
        [FromQuery] string? group = null,
        CancellationToken ct = default)
    {
        if (!TimeBuckets.TryParse(since, until, buckets, out var range, out var error))
            return BadRequest(new { error });

        List<JobExecution> runs;
        try
        {
            runs = await JobStatistics.CollectAsync(_jobs, await ResolveJobIdsAsync(job, group, ct), range!, ct);
        }
        catch (NotSupportedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new { error = ex.Message, code = "NOT_SUPPORTED" });
        }

        return Ok(JobStatistics.Compute(runs, range!));
    }

    /// <summary>
    /// The jobs named in <paramref name="job"/> (repeated or comma-separated) and/or in <paramref name="group"/>
    /// (case-insensitive): null for every job, empty when the filters match none.
    /// </summary>
    private async Task<List<string>?> ResolveJobIdsAsync(string[]? job, string? group, CancellationToken ct)
    {
        var jobIds = job?.SelectMany(j => j.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        if (!string.IsNullOrWhiteSpace(group))
        {
            var inGroup = (await _jobs.GetJobsAsync(ct))
                .Where(j => string.Equals(j.Group, group, StringComparison.OrdinalIgnoreCase))
                .Select(j => j.Id);
            jobIds = jobIds is { Count: > 0 } ? jobIds.Intersect(inGroup).ToList() : inGroup.ToList();
        }

        return jobIds is { Count: > 0 } || group != null ? jobIds : null;
    }

    /// <summary>
    /// Retrieves details of a specific job execution, including logs.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="execId">The execution identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    [HttpGet("{id}/history/{execId}")]
    public async Task<IActionResult> GetExecution(string id, string execId, CancellationToken ct)
    {
        var execution = await _jobs.GetExecutionAsync(id, execId, ct);
        if (execution == null || execution.JobId != id)
            return NotFound(new { error = "Execution not found", code = "EXECUTION_NOT_FOUND" });

        return Ok(new
        {
            id = execution.Id,
            jobId = execution.JobId,
            jobName = execution.JobName,
            status = execution.Status.ToString().ToLowerInvariant(),
            startedAt = execution.StartedAt,
            completedAt = execution.CompletedAt,
            duration = execution.Duration,
            error = execution.Error,
            retryAttempt = execution.RetryAttempt,
            properties = execution.Properties,
            logs = execution.Logs.Select(l => new { l.Timestamp, l.Level, l.Message }),
        });
    }

    /// <summary>
    /// Runs a job action and maps the manager's exceptions: unknown job 404, refused by the job's state 409,
    /// not offered by the manager 501.
    /// </summary>
    private async Task<IActionResult> RunAsync(Func<Task> action, string done, string conflictCode)
    {
        try
        {
            await action();
            return Ok(new { message = done });
        }
        catch (KeyNotFoundException)
        {
            return JobNotFound();
        }
        catch (NotSupportedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new { error = ex.Message, code = "NOT_SUPPORTED" });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message, code = conflictCode });
        }
    }

    private NotFoundObjectResult JobNotFound() => NotFound(new { error = "Job not found", code = "JOB_NOT_FOUND" });

    internal static object MapJobDto(CliJobInfo j) => new
    {
        id = j.Id,
        name = j.Name,
        description = j.Description,
        group = j.Group,
        status = j.Status.ToString().ToLowerInvariant(),
        schedule = j.Schedule,
        interval = (long?)j.Interval?.TotalMilliseconds,
        enabled = j.Status != JobStatus.Stopped,
        maxRetries = j.MaxRetries,
        retryDelay = (long)j.RetryDelay.TotalMilliseconds,
        retryStrategy = j.RetryStrategy.ToString().ToLowerInvariant(),
        timeout = (long?)j.Timeout?.TotalMilliseconds,
        overlapPolicy = j.OverlapPolicy.ToString().ToLowerInvariant(),
        currentExecutionId = j.CurrentExecutionId,
        nextRunAt = j.NextRunAt,
        lastRunAt = j.LastRunAt,
        lastRunStatus = j.LastRunStatus,
        lastRunDuration = j.LastRunDuration,
        actions = j.Actions,
    };
}

/// <summary>
/// Request body for updating a job's configuration.
/// </summary>
public class UpdateJobRequest
{
    /// <summary>Human-readable description of the job.</summary>
    public string? Description { get; set; }
    /// <summary>Logical group name for organizing jobs.</summary>
    public string? Group { get; set; }
    /// <summary>Cron expression for scheduling (mutually exclusive with <see cref="Interval"/>).</summary>
    public string? Schedule { get; set; }
    /// <summary>Interval string (e.g., "30s", "5m") for periodic execution (mutually exclusive with <see cref="Schedule"/>).</summary>
    public string? Interval { get; set; }
    /// <summary>Maximum number of retry attempts on failure.</summary>
    public int? MaxRetries { get; set; }
    /// <summary>Delay between retries as an interval string (e.g., "10s").</summary>
    public string? RetryDelay { get; set; }
    /// <summary>Retry strategy: "fixed", "linear", or "exponential".</summary>
    public string? RetryStrategy { get; set; }
    /// <summary>Execution timeout as an interval string (e.g., "5m").</summary>
    public string? Timeout { get; set; }
    /// <summary>Overlap policy: "skip", "cancel", or "queue".</summary>
    public string? OverlapPolicy { get; set; }
}
