using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Databases;
using Qodalis.Cli.Security;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// The databases plugin on a server: API, stats, route permissions, terminal commands, MCP tools, the run event, the
/// audit entry and the startup apply (SQLite).
/// </summary>
public sealed class DatabasesPluginTests : IAsyncLifetime
{
    private const string Api = "databases";
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"qcli-databases-plugin-{Guid.NewGuid():N}.db");
    private UsersTestHost? _h;

    private static readonly List<CliMigration> Notes =
    [
        new() { Id = "1", Name = "create notes", Up = "CREATE TABLE \"Notes\" (\"Id\" INTEGER PRIMARY KEY)", Down = CliMigrationStep.FromSql("DROP TABLE \"Notes\"") },
        new() { Id = "2", Name = "create tags", Up = "CREATE TABLE \"Tags\" (\"Id\" INTEGER PRIMARY KEY)", Down = CliMigrationStep.FromSql("DROP TABLE \"Tags\"") },
    ];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_h != null) await _h.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private async Task<UsersTestHost> StartAsync(bool protect = false, IReadOnlyList<CliMigration>? list = null, bool onStartup = false,
        Action<CliBuilder>? configure = null)
    {
        _h = await UsersTestHost.StartAsync(o => o.ProtectServer = protect, configureCli: cli => (configure ?? (_ => { }))(cli
            .AddMcp()
            .AddDatabases(d => d
                .AddDatabase("main", db => db.UseConnection(_ => new SqliteConnection($"Data Source={_file}")).ApplyMigrationsOnStartup(onStartup))
                .AddMigrations("main", "app", list ?? Notes))));
        return _h;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage res, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.Equal(status, res.StatusCode);
        return await UsersTestHost.ReadJsonAsync(res);
    }

    [Fact]
    public async Task ListsShowsRunsAndRollsBack_ThroughTheApi_PublishingAndAuditingEachRun()
    {
        var h = await StartAsync();
        var events = new List<string>();
        h.Host.Services.GetRequiredService<Abstractions.Events.ICliEventBus>()
            .Subscribe("databases:*", (e, _) =>
            {
                lock (events) events.Add($"{e.Type} {JsonSerializer.Serialize(e.Data)}");
                return Task.CompletedTask;
            });

        var overview = await JsonAsync(await h.GetAsync(Api));
        var db = Assert.Single(overview.GetProperty("databases").EnumerateArray());
        Assert.Equal("main", db.GetProperty("name").GetString());
        Assert.Equal("sqlite", db.GetProperty("dialect").GetString());
        Assert.Equal(2, db.GetProperty("pending").GetInt32());
        Assert.False(db.TryGetProperty("lastRun", out _));
        Assert.True(db.GetProperty("sizeBytes").GetInt64() >= 0);
        Assert.Equal(2, (await JsonAsync(await h.GetAsync($"{Api}/main"))).GetProperty("pending").GetInt32());

        var detail = await JsonAsync(await h.GetAsync($"{Api}/main/migrations"));
        Assert.Equal(["1:pending", "2:pending"], detail.GetProperty("migrations").EnumerateArray()
            .Select(m => $"{m.GetProperty("id").GetString()}:{m.GetProperty("status").GetString()}"));

        var sql = await JsonAsync(await h.GetAsync($"{Api}/main/migrations/sql?to=1"));
        Assert.Contains("CREATE TABLE \"Notes\"", sql.GetProperty("sql").GetString());
        Assert.DoesNotContain("\"Tags\"", sql.GetProperty("sql").GetString());

        var up = await JsonAsync(await h.PostAsync($"{Api}/main/migrations/up", null));
        Assert.Equal("succeeded", up.GetProperty("status").GetString());
        Assert.Equal("api", up.GetProperty("trigger").GetString());
        Assert.Equal(["1", "2"], up.GetProperty("applied").EnumerateArray().Select(a => a.GetProperty("id").GetString()));
        lock (events) Assert.Contains(events, e => e.StartsWith("databases:migration-run ") && e.Contains("\"count\":2") && e.Contains("\"database\":\"main\""));
        var audit = await h.Host.Services.GetRequiredService<IAuditStore>().GetEntriesAsync(new AuditQueryOptions { Event = "migrations.up" });
        Assert.Equal("database:main", Assert.Single(audit).Target);
        Assert.Equal("app:1, app:2", audit[0].Details);

        var down = await JsonAsync(await h.PostAsync($"{Api}/main/migrations/down", null, new { to = "1" }));
        Assert.Equal(["2"], down.GetProperty("applied").EnumerateArray().Select(a => a.GetProperty("id").GetString()));
        Assert.Equal("to is required to roll back (an id, or base for everything)",
            await UsersTestHost.AssertErrorAsync(await h.PostAsync($"{Api}/main/migrations/down", null), 400));

        var runs = await JsonAsync(await h.GetAsync($"{Api}/main/migrations/runs?size=1"));
        Assert.Equal(2, runs.GetProperty("total").GetInt32());
        Assert.Equal("down", Assert.Single(runs.GetProperty("items").EnumerateArray()).GetProperty("direction").GetString());
        Assert.Equal("unknown database: nope", await UsersTestHost.AssertErrorAsync(await h.GetAsync($"{Api}/nope"), 404));
        Assert.Equal("unknown migration: 9", await UsersTestHost.AssertErrorAsync(await h.PostAsync($"{Api}/main/migrations/up", null, new { to = "9" }), 404));
        await UsersTestHost.AssertErrorAsync(await h.PostAsync($"{Api}/main/migrations/up", null, new { to = new string('x', 300) }), 400);
    }

    [Fact]
    public async Task Stats_ShowSizeVersionAndLargestTables()
    {
        var h = await StartAsync(onStartup: true);
        var conn = new SqliteConnection($"Data Source={_file}");
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO \"Notes\" (\"Id\") VALUES (1), (2), (3)";
            await cmd.ExecuteNonQueryAsync();
        }
        await conn.DisposeAsync();

        var stats = await JsonAsync(await h.GetAsync($"{Api}/main/stats"));
        Assert.Equal("main", stats.GetProperty("name").GetString());
        Assert.Equal("sqlite", stats.GetProperty("dialect").GetString());
        Assert.StartsWith("SQLite 3.", stats.GetProperty("server").GetProperty("version").GetString());
        Assert.True(stats.GetProperty("size").GetProperty("bytes").GetInt64() > 0);
        Assert.False(stats.TryGetProperty("connections", out _));
        Assert.Empty(stats.GetProperty("unavailable").EnumerateArray());
        var tables = stats.GetProperty("tables").EnumerateArray().ToList();
        Assert.Contains(tables, t => t.GetProperty("name").GetString() == "Notes" && t.GetProperty("rows").GetInt64() == 3);
        Assert.Contains(tables, t => t.GetProperty("name").GetString() == "QcliMigrations");
        Assert.Equal("unknown database: nope", await UsersTestHost.AssertErrorAsync(await h.GetAsync($"{Api}/nope/stats"), 404));
    }

    [Fact]
    public async Task OnAProtectedServer_ReadingNeedsRead_AndRunningNeedsMigrate()
    {
        var h = await StartAsync(protect: true);
        var root = await h.RootTokenAsync();
        var viewer = await h.UserWithPermissionsAsync(root, "viewer", DatabasesPermissions.Read);

        await UsersTestHost.AssertErrorAsync(await h.GetAsync(Api), 401);
        Assert.Equal(HttpStatusCode.OK, (await h.GetAsync(Api, viewer)).StatusCode);
        Assert.Equal("permission denied: databases:migrate", await UsersTestHost.AssertErrorAsync(await h.PostAsync($"{Api}/main/migrations/up", viewer), 403));
        var up = await JsonAsync(await h.PostAsync($"{Api}/main/migrations/up", root));
        Assert.Equal("root", up.GetProperty("actor").GetString());

        var catalog = h.Host.Services.GetRequiredService<ICliPermissionCatalog>();
        Assert.NotNull(catalog.Find(DatabasesPermissions.Read));
        Assert.NotNull(catalog.Find(DatabasesPermissions.Migrate));
    }

    private static object Exec(string sub, string? value = null, Dictionary<string, object>? args = null) => new
    {
        command = "databases",
        chainCommands = sub.Length == 0 ? Array.Empty<string>() : [sub],
        value,
        args = args ?? new Dictionary<string, object>(),
        rawCommand = $"databases {sub} {value}".Trim(),
    };

    private static async Task<string> OutputAsync(HttpResponseMessage res) => (await JsonAsync(res)).ToString();

    [Fact]
    public async Task TerminalCommands_ShowRunAndRollBack()
    {
        var h = await StartAsync();
        Assert.Matches(@"main \(sqlite, [0-9.]+ K?B\): 0 applied, 2 pending", await OutputAsync(await h.PostAsync("execute", null, Exec(""))));
        Assert.Matches(@"main \(sqlite, [0-9.]+ K?B\): 0 applied, 2 pending", await OutputAsync(await h.PostAsync("execute", null, Exec("list"))));
        Assert.Contains("CREATE TABLE", await OutputAsync(await h.PostAsync("execute", null, Exec("up", "main", new() { ["dry-run"] = true }))));
        Assert.Contains("Applied 1 migration(s) (succeeded)", await OutputAsync(await h.PostAsync("execute", null, Exec("up", "main", new() { ["to"] = "1" }))));
        Assert.Matches(@"applied\s+app:1 create notes", await OutputAsync(await h.PostAsync("execute", null, Exec("migrations", "main"))));
        Assert.Contains("--to is required", await OutputAsync(await h.PostAsync("execute", null, Exec("down", "main"))));
        Assert.Contains("Rolled back 1 migration(s)", await OutputAsync(await h.PostAsync("execute", null, Exec("down", "main", new() { ["to"] = "base" }))));
        var runs = await h.Host.Services.GetRequiredService<CliDatabases>().Require("main").RunsAsync();
        Assert.All(runs.Items, r => Assert.Equal("terminal", r.Trigger));
        var stats = await OutputAsync(await h.PostAsync("execute", null, Exec("stats", "main")));
        Assert.Contains("Server:      SQLite 3.", stats);
        Assert.Contains("Size:        ", stats);
    }

    [Fact]
    public async Task McpTools_ShowRunAndRollBack()
    {
        var h = await StartAsync();
        var names = await Mcp.ToolNamesAsync(h.Client);
        Assert.Contains("databases_list", names);
        Assert.Contains("databases_stats", names);
        Assert.Contains("migrations_status", names);
        Assert.Contains("migrations_down", names);

        Assert.Matches(@"^main \(sqlite, [0-9.]+ K?B\): 0 applied, 2 pending$", Mcp.Text(await Mcp.CallAsync(h.Client, "migrations_status")));
        Assert.Matches(@"^main \(sqlite, [0-9.]+ K?B\): 0 applied, 2 pending$", Mcp.Text(await Mcp.CallAsync(h.Client, "databases_list")));
        var stats = await Mcp.CallAsync(h.Client, "databases_stats", new { database = "main" });
        Assert.StartsWith("main (sqlite)", Mcp.Text(stats));
        Assert.True(stats.GetProperty("structuredContent").GetProperty("size").GetProperty("bytes").GetInt64() >= 0);
        Assert.Contains("CREATE TABLE \"Notes\"", Mcp.Text(await Mcp.CallAsync(h.Client, "migrations_sql", new { database = "main", to = "1" })));
        var up = await Mcp.CallAsync(h.Client, "migrations_up", new { database = "main" });
        Assert.Equal("Applied 2 migration(s) (succeeded)", Mcp.Text(up));
        Assert.Equal("mcp", up.GetProperty("structuredContent").GetProperty("trigger").GetString());
        Assert.Matches(@"applied\s+app:2 create tags", Mcp.Text(await Mcp.CallAsync(h.Client, "migrations_status", new { database = "main" })));
        Assert.Equal("Rolled back 2 migration(s) (succeeded)", Mcp.Text(await Mcp.CallAsync(h.Client, "migrations_down", new { database = "main", to = "base" })));
        Assert.Equal(2, (await Mcp.CallAsync(h.Client, "migrations_runs", new { database = "main" })).GetProperty("structuredContent").GetProperty("total").GetInt32());
        var unknown = await Mcp.CallAsync(h.Client, "migrations_status", new { database = "nope" });
        Assert.True(unknown.GetProperty("isError").GetBoolean());
        Assert.Equal("unknown database: nope", Mcp.Text(unknown));
    }

    [Fact]
    public async Task AppliesOnStartup()
    {
        var h = await StartAsync(onStartup: true);
        var states = await h.Host.Services.GetRequiredService<CliDatabases>().Require("main").StatusAsync();
        Assert.All(states, s => Assert.Equal("applied", s.Status));
        var run = (await h.Host.Services.GetRequiredService<CliDatabases>().Require("main").RunsAsync()).Items[0];
        Assert.Equal("startup", run.Trigger);
    }

    [Fact]
    public async Task AFailingStartupMigration_StopsTheServer()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartAsync(list: [new CliMigration { Id = "1", Name = "broken", Up = "CREATE TABLE" }], onStartup: true));
        Assert.StartsWith("migrations of main failed: app:1 broken: ", error.Message);
    }

    [Fact]
    public async Task ServesTheDashboardsDatabasesPage_ToAnyone_AndRegistersIt()
    {
        // A build of the page (the package embeds the real one when it was built with it)
        var page = Path.Combine(Path.GetTempPath(), $"qcli-databases-page-{Guid.NewGuid():N}");
        Directory.CreateDirectory(page);
        File.WriteAllText(Path.Combine(page, "extension.js"), "export default {};");
        using var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(page);
        var h = await StartAsync(protect: true, configure: cli => cli.ConfigureDashboardModule("databases", files));
        var extension = Assert.Single(h.Host.Services.GetRequiredService<Qodalis.Cli.Models.CliDashboardExtensions>().All, e => e.Id == "databases");
        Assert.Equal("Databases", extension.Title);
        Assert.Equal("data", extension.Group);
        Assert.Equal(DatabasesPermissions.Read, extension.Permission);
        Assert.Equal("Baze de date", extension.Titles!["ro"]);
        Assert.Matches(@"^/qcli/databases/extension\.js\?v=[0-9a-f]{12}$", extension.Entry);
        Assert.Equal(["overview"], extension.Widgets);

        var js = await h.Client.GetAsync("/qcli/databases/extension.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Equal("no-cache", js.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.GetAsync(Api)).StatusCode);
        Directory.Delete(page, true);
    }

    [Fact]
    public void RoutePermissions_LongestPathWins_AndMustBeCliPaths()
    {
        var routes = new List<CliRoutePermission>
        {
            new("/api/v1/qcli/databases", "databases:read", "databases:migrate"),
            new("/api/v1/qcli/databases/special", "special:read"),
        };
        Assert.Equal("databases:read", CliRoutePolicy.Resolve("GET", "/api/v1/qcli/databases/main", routes).Permission);
        Assert.Equal("databases:migrate", CliRoutePolicy.Resolve("POST", "/api/v1/qcli/databases/main/migrations/up", routes).Permission);
        Assert.Equal("special:read", CliRoutePolicy.Resolve("POST", "/api/v1/qcli/databases/special/x", routes).Permission);
        Assert.Equal("jobs:read", CliRoutePolicy.Resolve("GET", "/api/v1/qcli/jobs", routes).Permission);
        Assert.Null(CliRoutePolicy.Resolve("GET", "/api/v1/qcli/databasesx", routes).Permission);

        var builder = new CliBuilder(new ServiceCollection());
        Assert.Throws<ArgumentException>(() => builder.AddRoutePermission("/api/other", "x:read"));
        Assert.Throws<ArgumentException>(() => builder.AddRoutePermission("/ws/v1/qcli/x", "x:read"));
        builder.AddRoutePermission("/api/v1/qcli/things/", "things:read").AddRoutePermission("/api/v1/qcli/things", "other:read");
        Assert.Equal("things:read", Assert.Single(builder.ServerOptions.RoutePermissions).Read);
    }
}
