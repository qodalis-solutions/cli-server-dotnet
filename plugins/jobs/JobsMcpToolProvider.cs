using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Mcp;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// MCP tools of the jobs plugin: <c>jobs_list</c>, <c>jobs_get</c>, <c>jobs_history</c> (<c>jobs:read</c>) and
/// <c>jobs_trigger</c>, <c>jobs_pause</c>, <c>jobs_resume</c>, <c>jobs_stop</c>, <c>jobs_cancel</c>, <c>jobs_delete_runs</c>
/// (<c>jobs:manage</c>).
/// They use the same <see cref="ICliJobManager"/> as <c>/api/v1/qcli/jobs</c>.
/// </summary>
public sealed class JobsMcpToolProvider : IMcpToolProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IServiceScopeFactory _scopes;

    public JobsMcpToolProvider(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    public string? Feature => "jobs";

    public Task<IReadOnlyList<McpToolDefinition>> GetToolsAsync(McpToolContext context, CancellationToken cancellationToken)
    {
        var id = new JsonObject { ["id"] = McpSchema.Property("string", "Job id or unique job name (see jobs_list)") };
        IReadOnlyList<McpToolDefinition> tools =
        [
            new()
            {
                Name = "jobs_list",
                Description = "List the scheduled jobs with their status and last run",
                InputSchema = McpSchema.Object(),
                RequiredPermissions = [CliRoutePolicy.JobsRead],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = Scoped((jobs, _, ct) => ListAsync(jobs, ct)),
            },
            new()
            {
                Name = "jobs_get",
                Description = "Show a job's configuration and status",
                InputSchema = McpSchema.Object((JsonObject)id.DeepClone(), "id"),
                RequiredPermissions = [CliRoutePolicy.JobsRead],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = Scoped((jobs, args, ct) => GetAsync(jobs, McpSchema.GetString(args, "id")!, ct)),
            },
            new()
            {
                Name = "jobs_history",
                Description = "Show the most recent executions of a job",
                InputSchema = McpSchema.Object(new JsonObject
                {
                    ["id"] = McpSchema.Property("string", "Job id or unique job name (see jobs_list)"),
                    ["limit"] = McpSchema.Property("integer", "Maximum number of executions (at most 100)", 20),
                }, "id"),
                RequiredPermissions = [CliRoutePolicy.JobsRead],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = Scoped(HistoryAsync),
            },
            Action("jobs_trigger", "Run a job now (queued when every slot is taken and the overlap policy is queue)",
                async (jobs, jid, ct) => await jobs.TriggerWithOutcomeAsync(jid, ct) == CliJobTriggerOutcome.Queued
                    ? "Job queued"
                    : "Job triggered",
                published: (events, jid) => events.TriggeredAsync(jid)),
            Action("jobs_pause", "Pause a job's schedule", "Job paused", (jobs, jid, ct) => jobs.PauseAsync(jid, ct)),
            Action("jobs_resume", "Resume a paused job", "Job resumed", (jobs, jid, ct) => jobs.ResumeAsync(jid, ct)),
            Action("jobs_stop", "Stop a job (cancels a running execution and disables its schedule)", "Job stopped",
                (jobs, jid, ct) => jobs.StopAsync(jid, ct), McpToolAnnotations.Destructive),
            Action("jobs_cancel", "Cancel a job's running execution", "Execution cancelled",
                (jobs, jid, ct) => jobs.CancelAsync(jid, ct), McpToolAnnotations.Destructive),
            new()
            {
                Name = "jobs_delete_runs",
                Description = "Delete a job's finished runs and their logs: one run (executionId), the runs with a status (e.g. failed), or all of them",
                InputSchema = McpSchema.Object(new JsonObject
                {
                    ["id"] = McpSchema.Property("string", "Job id or unique job name (see jobs_list)"),
                    ["executionId"] = McpSchema.Property("string", "Delete only this run (see jobs_history)"),
                    ["status"] = McpSchema.Property("string", "Delete only the runs with this status: completed, failed, cancelled or timedout"),
                }, "id"),
                RequiredPermissions = [CliRoutePolicy.JobsManage],
                Annotations = McpToolAnnotations.Destructive,
                CallAsync = Scoped(DeleteRunsAsync),
            },
        ];
        return Task.FromResult(tools);
    }

    private McpToolDefinition Action(string name, string description, string done,
        Func<ICliJobManager, string, CancellationToken, Task> run, McpToolAnnotations? annotations = null,
        Func<JobEvents, string, Task>? published = null) =>
        Action(name, description, async (jobs, jid, ct) =>
        {
            await run(jobs, jid, ct);
            return done;
        }, annotations, published);

    /// <summary>A job action whose answer (<c>"{answer}: {jobId}"</c>) comes from the action.</summary>
    private McpToolDefinition Action(string name, string description,
        Func<ICliJobManager, string, CancellationToken, Task<string>> run, McpToolAnnotations? annotations = null,
        Func<JobEvents, string, Task>? published = null) => new()
    {
        Name = name,
        Description = description,
        InputSchema = McpSchema.Object(new JsonObject { ["id"] = McpSchema.Property("string", "Job id or unique job name (see jobs_list)") }, "id"),
        RequiredPermissions = [CliRoutePolicy.JobsManage],
        Annotations = annotations,
        CallAsync = Scoped(async (jobs, events, args, ct) =>
        {
            var (jobId, error) = await ResolveAsync(jobs, McpSchema.GetString(args, "id")!, ct);
            if (error != null) return error;
            try
            {
                var done = await run(jobs, jobId!, ct);
                if (published != null) await published(events, jobId!);
                return McpToolResult.Ok($"{done}: {jobId}");
            }
            catch (KeyNotFoundException)
            {
                return McpToolResult.Error($"Job not found: {jobId}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                return McpToolResult.Error(ex.Message);
            }
        }),
    };

    private static async Task<McpToolResult> DeleteRunsAsync(ICliJobManager jobs, JobEvents events, JsonObject args,
        CancellationToken ct)
    {
        var (jobId, error) = await ResolveAsync(jobs, McpSchema.GetString(args, "id")!, ct);
        if (error != null) return error;
        var executionId = McpSchema.GetString(args, "executionId");
        try
        {
            if (!string.IsNullOrEmpty(executionId))
            {
                await jobs.DeleteExecutionAsync(jobId!, executionId, ct);
                await events.RunDeletedAsync(jobId!, executionId);
                return McpToolResult.Ok($"Run deleted: {executionId}");
            }
            var status = McpSchema.GetString(args, "status");
            var deleted = await jobs.DeleteExecutionsAsync(jobId!, string.IsNullOrWhiteSpace(status) ? null : status, ct);
            await events.RunsDeletedAsync(jobId!, deleted);
            return McpToolResult.Ok($"Deleted {deleted} run(s) of {jobId}");
        }
        catch (KeyNotFoundException)
        {
            return McpToolResult.Error(string.IsNullOrEmpty(executionId) ? $"Job not found: {jobId}" : $"Run not found: {executionId}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return McpToolResult.Error(ex.Message);
        }
    }

    /// <summary>Runs a tool call with the job manager of a new scope (the manager may be scoped).</summary>
    private Func<JsonObject, McpToolContext, CancellationToken, Task<McpToolResult>> Scoped(
        Func<ICliJobManager, JsonObject, CancellationToken, Task<McpToolResult>> call) =>
        Scoped((jobs, _, args, ct) => call(jobs, args, ct));

    /// <summary>Runs a tool call with the job manager of a new scope and the job events of the caller.</summary>
    private Func<JsonObject, McpToolContext, CancellationToken, Task<McpToolResult>> Scoped(
        Func<ICliJobManager, JobEvents, JsonObject, CancellationToken, Task<McpToolResult>> call) => async (args, context, ct) =>
    {
        await using var scope = _scopes.CreateAsyncScope();
        var events = new JobEvents(scope.ServiceProvider.GetService<ICliEventBus>(), context.Principal?.UserName);
        return await call(scope.ServiceProvider.GetRequiredService<ICliJobManager>(), events, args, ct);
    };

    private static async Task<McpToolResult> ListAsync(ICliJobManager jobManager, CancellationToken ct)
    {
        var jobs = await jobManager.GetJobsAsync(ct);
        var node = JsonSerializer.SerializeToNode(jobs.Select(CliJobsController.MapJobDto).ToList(), Json)!;
        var text = jobs.Count == 0
            ? "(no jobs)"
            : string.Join('\n', jobs.Select(j =>
                $"{j.Id} | {j.Name} | {j.Status.ToString().ToLowerInvariant()} | last run: {j.LastRunStatus ?? "-"}"));
        return McpToolResult.Ok(text, new JsonObject { ["jobs"] = node });
    }

    /// <summary>Resolves a job id, or a job name matching exactly one job (case-insensitive).</summary>
    private static async Task<(string? Id, McpToolResult? Error)> ResolveAsync(ICliJobManager jobManager, string idOrName,
        CancellationToken ct)
    {
        var jobs = await jobManager.GetJobsAsync(ct);
        if (jobs.Any(j => j.Id == idOrName)) return (idOrName, null);
        var matches = jobs
            .Where(j => string.Equals(j.Name, idOrName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count switch
        {
            1 => (matches[0].Id, null),
            0 => (null, McpToolResult.Error($"Job not found: {idOrName}")),
            _ => (null, McpToolResult.Error($"Job name '{idOrName}' is ambiguous: use the job id")),
        };
    }

    private static async Task<McpToolResult> GetAsync(ICliJobManager jobs, string idOrName, CancellationToken ct)
    {
        var (id, error) = await ResolveAsync(jobs, idOrName, ct);
        if (error != null) return error;
        var job = await jobs.GetJobAsync(id!, ct);
        if (job == null) return McpToolResult.Error($"Job not found: {id}");
        var node = (JsonObject)JsonSerializer.SerializeToNode(CliJobsController.MapJobDto(job), Json)!;
        return McpToolResult.Ok(node.ToJsonString(Json), node);
    }

    private static async Task<McpToolResult> HistoryAsync(ICliJobManager jobs, JsonObject args, CancellationToken ct)
    {
        var (id, error) = await ResolveAsync(jobs, McpSchema.GetString(args, "id")!, ct);
        if (error != null) return error;
        var limit = (int)Math.Clamp(McpSchema.GetInt64(args, "limit") ?? 20, 1, 100);

        var (items, total) = await jobs.GetExecutionsAsync(id!, limit, 0, null, ct);
        var executions = items.Select(e => new
        {
            id = e.Id,
            status = e.Status.ToString().ToLowerInvariant(),
            startedAt = e.StartedAt,
            completedAt = e.CompletedAt,
            duration = e.Duration,
            error = e.Error,
            retryAttempt = e.RetryAttempt,
            properties = e.Properties,
        }).ToList();
        var text = executions.Count == 0
            ? "(no executions)"
            : string.Join('\n', executions.Select(e =>
                $"{e.startedAt:O} | {e.status} | {e.duration?.ToString() ?? "-"} ms{(e.error != null ? $" | {e.error}" : "")}"));
        return McpToolResult.Ok(text, new JsonObject
        {
            ["items"] = JsonSerializer.SerializeToNode(executions, Json),
            ["total"] = total,
        });
    }
}
