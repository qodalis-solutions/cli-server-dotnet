using System.Text.Json;
using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Mcp;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// MCP tools of the jobs plugin: <c>jobs_list</c>, <c>jobs_get</c>, <c>jobs_history</c> (<c>jobs:read</c>) and
/// <c>jobs_trigger</c>, <c>jobs_pause</c>, <c>jobs_resume</c>, <c>jobs_stop</c>, <c>jobs_cancel</c> (<c>jobs:manage</c>).
/// They use the same <see cref="CliJobScheduler"/> and storage as <c>/api/v1/qcli/jobs</c>.
/// </summary>
public sealed class JobsMcpToolProvider : IMcpToolProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly CliJobScheduler _scheduler;
    private readonly ICliJobStorageProvider _storage;

    public JobsMcpToolProvider(CliJobScheduler scheduler, ICliJobStorageProvider storage)
    {
        _scheduler = scheduler;
        _storage = storage;
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
                CallAsync = (_, _, _) => Task.FromResult(List()),
            },
            new()
            {
                Name = "jobs_get",
                Description = "Show a job's configuration and status",
                InputSchema = McpSchema.Object((JsonObject)id.DeepClone(), "id"),
                RequiredPermissions = [CliRoutePolicy.JobsRead],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = (args, _, _) => Task.FromResult(Get(McpSchema.GetString(args, "id")!)),
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
                CallAsync = HistoryAsync,
            },
            Action("jobs_trigger", "Run a job now", "Job triggered", (jid, ct) => _scheduler.TriggerAsync(jid, ct)),
            Action("jobs_pause", "Pause a job's schedule", "Job paused", (jid, ct) => _scheduler.PauseAsync(jid, ct)),
            Action("jobs_resume", "Resume a paused job", "Job resumed", (jid, ct) => _scheduler.ResumeAsync(jid, ct)),
            Action("jobs_stop", "Stop a job (cancels a running execution and disables its schedule)", "Job stopped",
                (jid, ct) => _scheduler.StopJobAsync(jid, ct), McpToolAnnotations.Destructive),
            Action("jobs_cancel", "Cancel a job's running execution", "Execution cancelled",
                (jid, ct) => _scheduler.CancelCurrentAsync(jid, ct), McpToolAnnotations.Destructive),
        ];
        return Task.FromResult(tools);
    }

    private McpToolDefinition Action(string name, string description, string done,
        Func<string, CancellationToken, Task> run, McpToolAnnotations? annotations = null) => new()
    {
        Name = name,
        Description = description,
        InputSchema = McpSchema.Object(new JsonObject { ["id"] = McpSchema.Property("string", "Job id or unique job name (see jobs_list)") }, "id"),
        RequiredPermissions = [CliRoutePolicy.JobsManage],
        Annotations = annotations,
        CallAsync = async (args, _, ct) =>
        {
            var (jobId, error) = Resolve(McpSchema.GetString(args, "id")!);
            if (error != null) return error;
            try
            {
                await run(jobId!, ct);
                return McpToolResult.Ok($"{done}: {jobId}");
            }
            catch (KeyNotFoundException)
            {
                return McpToolResult.Error($"Job not found: {jobId}");
            }
            catch (InvalidOperationException ex)
            {
                return McpToolResult.Error(ex.Message);
            }
        },
    };

    private McpToolResult List()
    {
        var jobs = _scheduler.Jobs.Values.Select(CliJobsController.MapJobDto).ToList();
        var node = JsonSerializer.SerializeToNode(jobs, Json)!;
        var text = jobs.Count == 0
            ? "(no jobs)"
            : string.Join('\n', _scheduler.Jobs.Values.Select(r =>
                $"{r.Id} | {r.Options.Name} | {r.Status.ToString().ToLowerInvariant()} | last run: {r.LastRunStatus ?? "-"}"));
        return McpToolResult.Ok(text, new JsonObject { ["jobs"] = node });
    }

    /// <summary>Resolves a job id, or a job name matching exactly one job (case-insensitive).</summary>
    private (string? Id, McpToolResult? Error) Resolve(string idOrName)
    {
        if (_scheduler.Jobs.ContainsKey(idOrName)) return (idOrName, null);
        var matches = _scheduler.Jobs.Values
            .Where(r => string.Equals(r.Options.Name, idOrName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count switch
        {
            1 => (matches[0].Id, null),
            0 => (null, McpToolResult.Error($"Job not found: {idOrName}")),
            _ => (null, McpToolResult.Error($"Job name '{idOrName}' is ambiguous: use the job id")),
        };
    }

    private McpToolResult Get(string idOrName)
    {
        var (id, error) = Resolve(idOrName);
        if (error != null) return error;
        var reg = _scheduler.Jobs[id!];
        var node = (JsonObject)JsonSerializer.SerializeToNode(CliJobsController.MapJobDto(reg), Json)!;
        return McpToolResult.Ok(node.ToJsonString(Json), node);
    }

    private async Task<McpToolResult> HistoryAsync(JsonObject args, McpToolContext context, CancellationToken ct)
    {
        var (id, error) = Resolve(McpSchema.GetString(args, "id")!);
        if (error != null) return error;
        var limit = (int)Math.Clamp(McpSchema.GetInt64(args, "limit") ?? 20, 1, 100);

        var (items, total) = await _storage.GetExecutionsAsync(id!, limit, 0, null, ct);
        var executions = items.Select(e => new
        {
            id = e.Id,
            status = e.Status.ToString().ToLowerInvariant(),
            startedAt = e.StartedAt,
            completedAt = e.CompletedAt,
            duration = e.Duration,
            error = e.Error,
            retryAttempt = e.RetryAttempt,
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
