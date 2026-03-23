using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// REST API controller for managing scheduled jobs (list, trigger, pause, resume, stop, cancel, update, history).
/// </summary>
[ApiController]
[Route("api/v1/qcli/jobs")]
public class CliJobsController : ControllerBase
{
    private readonly CliJobScheduler _scheduler;
    private readonly ICliJobStorageProvider _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliJobsController"/> class.
    /// </summary>
    /// <param name="scheduler">The job scheduler service.</param>
    /// <param name="storage">The job storage provider for execution history.</param>
    public CliJobsController(CliJobScheduler scheduler, ICliJobStorageProvider storage)
    {
        _scheduler = scheduler;
        _storage = storage;
    }

    /// <summary>
    /// Lists all registered jobs with their current status and configuration.
    /// </summary>
    [HttpGet]
    public IActionResult ListJobs()
    {
        var jobs = _scheduler.Jobs.Values.Select(r => MapJobDto(r));
        return Ok(jobs);
    }

    /// <summary>
    /// Gets details of a specific job by ID.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    [HttpGet("{id}")]
    public IActionResult GetJob(string id)
    {
        if (!_scheduler.Jobs.TryGetValue(id, out var reg))
            return NotFound(new { error = "Job not found", code = "JOB_NOT_FOUND" });

        return Ok(MapJobDto(reg));
    }

    /// <summary>
    /// Manually triggers a job for immediate execution.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    [HttpPost("{id}/trigger")]
    public async Task<IActionResult> TriggerJob(string id, CancellationToken ct)
    {
        try
        {
            await _scheduler.TriggerAsync(id, ct);
            return Ok(new { message = "Job triggered" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Job not found", code = "JOB_NOT_FOUND" });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message, code = "JOB_ALREADY_RUNNING" });
        }
    }

    /// <summary>
    /// Pauses a job, preventing further scheduled executions.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    [HttpPost("{id}/pause")]
    public async Task<IActionResult> PauseJob(string id, CancellationToken ct)
    {
        try
        {
            await _scheduler.PauseAsync(id, ct);
            return Ok(new { message = "Job paused" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Job not found", code = "JOB_NOT_FOUND" });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message, code = "JOB_ALREADY_PAUSED" });
        }
    }

    /// <summary>
    /// Resumes a previously paused job.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    [HttpPost("{id}/resume")]
    public async Task<IActionResult> ResumeJob(string id, CancellationToken ct)
    {
        try
        {
            await _scheduler.ResumeAsync(id, ct);
            return Ok(new { message = "Job resumed" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Job not found", code = "JOB_NOT_FOUND" });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message, code = "JOB_NOT_PAUSED" });
        }
    }

    /// <summary>
    /// Stops a job entirely, cancelling any running execution.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    [HttpPost("{id}/stop")]
    public async Task<IActionResult> StopJob(string id, CancellationToken ct)
    {
        try
        {
            await _scheduler.StopJobAsync(id, ct);
            return Ok(new { message = "Job stopped" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Job not found", code = "JOB_NOT_FOUND" });
        }
    }

    /// <summary>
    /// Cancels the currently running execution of a job.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> CancelJob(string id, CancellationToken ct)
    {
        try
        {
            await _scheduler.CancelCurrentAsync(id, ct);
            return Ok(new { message = "Execution cancelled" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Job not found", code = "JOB_NOT_FOUND" });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message, code = "JOB_NOT_RUNNING" });
        }
    }

    /// <summary>
    /// Updates the configuration of a registered job (schedule, retries, timeout, etc.).
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
            await _scheduler.UpdateOptionsAsync(id, options =>
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
            }, ct);
            return Ok(new { message = "Job updated" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Job not found", code = "JOB_NOT_FOUND" });
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
        if (!_scheduler.Jobs.ContainsKey(id))
            return NotFound(new { error = "Job not found", code = "JOB_NOT_FOUND" });

        if (limit > 100) limit = 100;
        var (items, total) = await _storage.GetExecutionsAsync(id, limit, offset, status, ct);

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
            }),
            total,
            limit,
            offset,
        });
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
        var execution = await _storage.GetExecutionAsync(execId, ct);
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
            logs = execution.Logs.Select(l => new { l.Timestamp, l.Level, l.Message }),
        });
    }

    private static object MapJobDto(JobRegistration r) => new
    {
        id = r.Id,
        name = r.Options.Name,
        description = r.Options.Description,
        group = r.Options.Group,
        status = r.Status.ToString().ToLowerInvariant(),
        schedule = r.Options.Schedule,
        interval = (long?)r.Options.Interval?.TotalMilliseconds,
        enabled = r.Status != JobStatus.Stopped,
        maxRetries = r.Options.MaxRetries,
        retryDelay = (long)r.Options.RetryDelay.TotalMilliseconds,
        retryStrategy = r.Options.RetryStrategy.ToString().ToLowerInvariant(),
        timeout = (long?)r.Options.Timeout?.TotalMilliseconds,
        overlapPolicy = r.Options.OverlapPolicy.ToString().ToLowerInvariant(),
        currentExecutionId = r.CurrentExecutionId,
        nextRunAt = r.NextRunAt,
        lastRunAt = r.LastRunAt,
        lastRunStatus = r.LastRunStatus,
        lastRunDuration = r.LastRunDuration,
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
