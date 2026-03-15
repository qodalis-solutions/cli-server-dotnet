using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Jobs;

namespace Qodalis.Cli.Controllers;

[ApiController]
[Route("api/v1/qcli/jobs")]
public class CliJobsController : ControllerBase
{
    private readonly CliJobScheduler _scheduler;
    private readonly ICliJobStorageProvider _storage;

    public CliJobsController(CliJobScheduler scheduler, ICliJobStorageProvider storage)
    {
        _scheduler = scheduler;
        _storage = storage;
    }

    [HttpGet]
    public IActionResult ListJobs()
    {
        var jobs = _scheduler.Jobs.Values.Select(r => MapJobDto(r));
        return Ok(jobs);
    }

    [HttpGet("{id}")]
    public IActionResult GetJob(string id)
    {
        if (!_scheduler.Jobs.TryGetValue(id, out var reg))
            return NotFound(new { error = "Job not found", code = "JOB_NOT_FOUND" });

        return Ok(MapJobDto(reg));
    }

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
        interval = r.Options.Interval?.TotalMilliseconds,
        enabled = r.Status != JobStatus.Stopped,
        maxRetries = r.Options.MaxRetries,
        timeout = r.Options.Timeout?.TotalMilliseconds,
        overlapPolicy = r.Options.OverlapPolicy.ToString().ToLowerInvariant(),
        currentExecutionId = r.CurrentExecutionId,
        nextRunAt = r.NextRunAt,
        lastRunAt = r.LastRunAt,
        lastRunStatus = r.LastRunStatus,
        lastRunDuration = r.LastRunDuration,
    };
}

public class UpdateJobRequest
{
    public string? Description { get; set; }
    public string? Group { get; set; }
    public string? Schedule { get; set; }
    public string? Interval { get; set; }
    public int? MaxRetries { get; set; }
    public string? Timeout { get; set; }
    public string? OverlapPolicy { get; set; }
}
