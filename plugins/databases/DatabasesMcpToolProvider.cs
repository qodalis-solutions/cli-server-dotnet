using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Mcp;

namespace Qodalis.Cli.Plugin.Databases;

/// <summary>
/// MCP tools of the databases plugin: <c>databases_list</c>, <c>databases_stats</c>, <c>migrations_status</c>,
/// <c>migrations_runs</c>, <c>migrations_sql</c> (<c>databases:read</c>), <c>migrations_up</c> and
/// <c>migrations_down</c> (<c>databases:migrate</c>).
/// </summary>
public sealed class DatabasesMcpToolProvider(IServiceProvider services) : IMcpToolProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string? Feature => "databases";

    private CliDatabases Databases => services.GetRequiredService<CliDatabases>();

    public Task<IReadOnlyList<McpToolDefinition>> GetToolsAsync(McpToolContext context, CancellationToken cancellationToken)
    {
        JsonNode Database() => McpSchema.Property("string", "Database name (see databases_list)");
        IReadOnlyList<McpToolDefinition> tools =
        [
            new()
            {
                Name = "databases_list",
                Description = "The server's databases: dialect, size, applied, pending and problem migrations, last run",
                InputSchema = McpSchema.Object(new JsonObject()),
                RequiredPermissions = [DatabasesPermissions.Read],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = Guarded(async (_, _, ct) => await ListAsync(ct)),
            },
            new()
            {
                Name = "databases_stats",
                Description = "A database's size (data, indexes, log), connections (active, open, max), load counters since the server started (queries, transactions, rows read and written), cache hit ratio and largest tables",
                InputSchema = McpSchema.Object(new JsonObject { ["database"] = Database() }, "database"),
                RequiredPermissions = [DatabasesPermissions.Read],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = Guarded(async (args, _, ct) =>
                {
                    var stats = await Databases.StatsAsync(McpSchema.GetString(args, "database")!, ct);
                    return McpToolResult.Ok(CliDatabaseFormat.Stats(stats), (JsonObject)JsonSerializer.SerializeToNode(stats, Json)!);
                }),
            },
            new()
            {
                Name = "migrations_status",
                Description = "Without a database: every database with its applied, pending and problem counts. With one: its migrations and their status (applied, pending, changed, missing, failed)",
                InputSchema = McpSchema.Object(new JsonObject { ["database"] = Database() }),
                RequiredPermissions = [DatabasesPermissions.Read],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = Guarded(StatusAsync),
            },
            new()
            {
                Name = "migrations_runs",
                Description = "Run history of a database, newest first: direction, trigger, who, status, error and what each run applied",
                InputSchema = McpSchema.Object(new JsonObject
                {
                    ["database"] = Database(),
                    ["limit"] = McpSchema.Property("integer", "Maximum runs (1-100)", 20),
                }, "database"),
                RequiredPermissions = [DatabasesPermissions.Read],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = Guarded(async (args, _, ct) =>
                {
                    var limit = Math.Clamp(args["limit"]?.GetValue<int>() ?? 20, 1, 100);
                    var page = await Databases.Require(McpSchema.GetString(args, "database")!).RunsAsync(limit, 0, ct);
                    var node = new JsonObject
                    {
                        ["items"] = JsonSerializer.SerializeToNode(page.Items, Json), ["total"] = page.Total,
                    };
                    return McpToolResult.Ok(node.ToJsonString(Json), node);
                }),
            },
            new()
            {
                Name = "migrations_sql",
                Description = "The SQL a run would execute, without running it (dry run)",
                InputSchema = McpSchema.Object(new JsonObject
                {
                    ["database"] = Database(),
                    ["direction"] = McpSchema.Property("string", "up (default) or down"),
                    ["to"] = McpSchema.Property("string", "As for migrations_up / migrations_down"),
                }, "database"),
                RequiredPermissions = [DatabasesPermissions.Read],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = Guarded(async (args, _, ct) =>
                {
                    var down = McpSchema.GetString(args, "direction") == "down";
                    var sql = await Databases.Require(McpSchema.GetString(args, "database")!)
                        .ScriptAsync(down ? "down" : "up", Optional(McpSchema.GetString(args, "to")), ct);
                    return McpToolResult.Ok(sql.Length > 0 ? sql : down ? "Nothing to roll back" : "Nothing pending", new JsonObject { ["sql"] = sql });
                }),
            },
            new()
            {
                Name = "migrations_up",
                Description = "Apply the pending migrations of a database (all, or up to one), under its lock; refused while a migration is changed or missing unless force",
                InputSchema = McpSchema.Object(new JsonObject
                {
                    ["database"] = Database(),
                    ["to"] = McpSchema.Property("string", "Stop after this migration (id or source:id); omit for all pending"),
                    ["force"] = McpSchema.Property("boolean", "Run although a migration is changed or missing"),
                }, "database"),
                RequiredPermissions = [DatabasesPermissions.Migrate],
                CallAsync = Guarded((args, ctx, ct) => RunAsync("up", args, ctx, ct)),
            },
            new()
            {
                Name = "migrations_down",
                Description = "Roll back the migrations of a database applied after a migration (to), running their down scripts",
                InputSchema = McpSchema.Object(new JsonObject
                {
                    ["database"] = Database(),
                    ["to"] = McpSchema.Property("string", "Roll back to this migration, keeping it (id or source:id), or base for everything"),
                    ["force"] = McpSchema.Property("boolean", "Run although a migration is changed or missing"),
                }, "database", "to"),
                RequiredPermissions = [DatabasesPermissions.Migrate],
                Annotations = McpToolAnnotations.Destructive,
                CallAsync = Guarded((args, ctx, ct) => RunAsync("down", args, ctx, ct)),
            },
        ];
        return Task.FromResult(tools);
    }

    private static string? Optional(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static Func<JsonObject, McpToolContext, CancellationToken, Task<McpToolResult>> Guarded(
        Func<JsonObject, McpToolContext, CancellationToken, Task<McpToolResult>> call) => async (args, context, ct) =>
    {
        try
        {
            return await call(args, context, ct);
        }
        catch (Exception ex) when (ex is CliMigrationException or InvalidOperationException or System.Data.Common.DbException)
        {
            return McpToolResult.Error(ex.Message);
        }
    };

    private async Task<McpToolResult> StatusAsync(JsonObject args, McpToolContext context, CancellationToken ct)
    {
        var name = Optional(McpSchema.GetString(args, "database"));
        if (name == null) return await ListAsync(ct);
        var db = Databases.Require(name);
        var states = await db.StatusAsync(ct);
        var lines = string.Join('\n', states.Select(s => $"{s.Status,-8} {s.Source}:{s.Id} {s.Name}"));
        return McpToolResult.Ok(lines.Length > 0 ? lines : "No migrations", new JsonObject
        {
            ["name"] = db.Name,
            ["dialect"] = db.Dialect,
            ["sources"] = JsonSerializer.SerializeToNode(db.SourceNames, Json),
            ["migrations"] = JsonSerializer.SerializeToNode(states, Json),
        });
    }

    private async Task<McpToolResult> ListAsync(CancellationToken ct)
    {
        var databases = await Databases.OverviewAsync(ct);
        var text = string.Join('\n', databases.Select(d => d.Error != null
            ? $"{d.Name} ({d.Dialect}): error: {d.Error}"
            : $"{d.Name} ({d.Dialect}{(d.SizeBytes is { } size ? $", {CliDatabaseFormat.Bytes(size)}" : "")}): {d.Applied} applied, {d.Pending} pending{(d.Problems > 0 ? $", {d.Problems} problem(s)" : "")}"));
        return McpToolResult.Ok(text.Length > 0 ? text : "No databases",
            new JsonObject { ["databases"] = JsonSerializer.SerializeToNode(databases, Json) });
    }

    private async Task<McpToolResult> RunAsync(string direction, JsonObject args, McpToolContext context, CancellationToken ct)
    {
        var to = Optional(McpSchema.GetString(args, "to"));
        if (direction == "down" && to == null) return McpToolResult.Error("to is required to roll back (an id, or base for everything)");
        var run = await Databases.RunAsync(McpSchema.GetString(args, "database")!, direction, new CliMigrationRunOptions
        {
            To = to,
            Force = args["force"]?.GetValue<bool>() == true,
            Actor = context.Principal?.UserName,
            Trigger = "mcp",
        }, ct);
        var text = $"{(direction == "up" ? "Applied" : "Rolled back")} {run.Applied.Count} migration(s) ({run.Status}){(run.Error != null ? $": {run.Error}" : "")}";
        var structured = (JsonObject)JsonSerializer.SerializeToNode(run, Json)!;
        return run.Status == "failed"
            ? new McpToolResult { Text = text, Structured = structured, IsError = true }
            : McpToolResult.Ok(text, structured);
    }
}
