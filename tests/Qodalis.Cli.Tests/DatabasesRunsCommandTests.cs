using System.Text.Json;
using Microsoft.Data.Sqlite;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Databases;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Config;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// <c>databases runs [&lt;db&gt;] [-n N] [--json]</c>: the migration run history through <c>POST /execute</c>, empty
/// history, several databases, bad limits, unknown databases, and databases:read on a protected server (SQLite).
/// </summary>
public sealed class DatabasesRunsCommandTests : IAsyncLifetime
{
    private static readonly List<CliMigration> Migrations =
    [
        new() { Id = "1", Name = "create notes", Up = "CREATE TABLE \"Notes\" (\"Id\" INTEGER PRIMARY KEY)", Down = CliMigrationStep.FromSql("DROP TABLE \"Notes\"") },
        new() { Id = "2", Name = "create tags", Up = "CREATE TABLE \"Tags\" (\"Id\" INTEGER PRIMARY KEY)", Down = CliMigrationStep.FromSql("DROP TABLE \"Tags\"") },
        new() { Id = "3", Name = "broken", Up = "CREATE TABLE \"Tags\" (\"Id\" INTEGER PRIMARY KEY)", Down = CliMigrationStep.FromSql("SELECT 1") },
    ];

    private readonly List<string> _files = [];
    private readonly List<IAsyncDisposable> _hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts) await host.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var file in _files) File.Delete(file);
    }

    private Action<CliBuilder> Databases(params string[] names) => cli => cli.AddDatabases(d =>
    {
        foreach (var name in names.Length == 0 ? ["main"] : names)
        {
            var file = Path.Combine(Path.GetTempPath(), $"qcli-runs-{name}-{Guid.NewGuid():N}.db");
            _files.Add(file);
            d.AddDatabase(name, db => db.UseConnection(_ => new SqliteConnection($"Data Source={file}"))).AddMigrations(name, "app", Migrations);
        }
    });

    private async Task<ConfigTestHost> StartAsync(params string[] names)
    {
        var host = await ConfigTestHost.StartAsync(Databases(names));
        _hosts.Add(host);
        return host;
    }

    private static Task<(int Status, string Text, JsonElement Body)> Run(ConfigTestHost h, string line) =>
        ConfigCommands.RunAsync(h.Client, h.Service<ICliCommandRegistry>(), line);

    [Fact]
    public async Task IsASubCommandOfDatabases_NeedingDatabasesRead()
    {
        var h = await StartAsync();
        var runs = h.Service<ICliCommandRegistry>().FindProcessor("databases", ["runs"]);
        Assert.Equal("runs", runs!.Command);
        Assert.Equal(["databases:read"], runs.RequiredPermissions!);
    }

    [Fact]
    public async Task SaysSo_WhenNothingRanYet()
    {
        var h = await StartAsync();
        Assert.Equal("main: no migration runs yet", (await Run(h, "databases runs")).Text);
        var json = JsonDocument.Parse((await Run(h, "databases runs main --json")).Text).RootElement;
        Assert.Equal("main", json.GetProperty("database").GetString());
        Assert.Equal(0, json.GetProperty("items").GetArrayLength());
        Assert.Equal(0, json.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ListsTheRunsNewestFirst_WithDirectionStatusCountTriggerActor_AndTheErrorOfAFailedOne()
    {
        var h = await StartAsync();
        var databases = h.Service<CliDatabases>();
        await databases.RunAsync("main", "up", new CliMigrationRunOptions { To = "2", Trigger = "terminal", Actor = "alice" });
        await Task.Delay(20);
        await databases.RunAsync("main", "up", new CliMigrationRunOptions { Trigger = "api" });

        var (_, text, body) = await Run(h, "databases runs main");
        Assert.Equal(0, body.GetProperty("exitCode").GetInt32());
        var lines = text.Split('\n');
        Assert.Equal("main (sqlite): 2 of 2 run(s)", lines[0]);
        Assert.Matches(@"^Started\s+Dir\s+Status\s+Count\s+Trigger$", lines[1]);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d up\s+failed\s+0\s+api\s+.*already exists", lines[2]);
        Assert.Matches(@"up\s+succeeded\s+2\s+terminal \(alice\)\s+to 2$", lines[3]);
    }

    [Fact]
    public async Task LimitCapsTheList_AndReportsTheTotal_JsonPrintsThePayload()
    {
        var h = await StartAsync();
        var databases = h.Service<CliDatabases>();
        await databases.RunAsync("main", "up", new CliMigrationRunOptions { To = "1" });
        await Task.Delay(20);
        await databases.RunAsync("main", "up", new CliMigrationRunOptions { To = "2" });
        await Task.Delay(20);
        await databases.RunAsync("main", "down", new CliMigrationRunOptions { To = "base" });

        Assert.Equal("main (sqlite): 1 of 3 run(s)", (await Run(h, "databases runs -n 1")).Text.Split('\n')[0]);
        Assert.Equal(4, (await Run(h, "databases runs --limit 2")).Text.Split('\n').Length);
        var json = JsonDocument.Parse((await Run(h, "databases runs main -n 2 --json")).Text).RootElement;
        Assert.Equal(3, json.GetProperty("total").GetInt32());
        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
        var first = json.GetProperty("items")[0];
        Assert.Equal("down", first.GetProperty("direction").GetString());
        Assert.Equal("succeeded", first.GetProperty("status").GetString());
        Assert.Equal("base", first.GetProperty("target").GetString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("abc")]
    [InlineData("2.5")]
    [InlineData("-3")]
    public async Task RefusesABadLimit(string bad)
    {
        var h = await StartAsync();
        Assert.Equal("Error: --limit must be a whole number from 1 to 100", (await Run(h, $"databases runs main --limit {bad}")).Text);
    }

    [Fact]
    public async Task NeedsTheDatabase_WhenThereAreSeveral_AndRefusesAnUnknownOne()
    {
        var h = await StartAsync("main", "logs");
        Assert.Equal("Error: name the database: main, logs", (await Run(h, "databases runs")).Text);
        Assert.Equal("Error: unknown database: nope", (await Run(h, "databases runs nope")).Text);
        Assert.Equal("logs: no migration runs yet", (await Run(h, "databases runs logs")).Text);
    }

    [Fact]
    public void FormatsARun_WithoutAnActorTargetOrError()
    {
        var run = new CliMigrationRun { Id = "1", Direction = "down", Trigger = "startup", StartedAt = "2026-11-25T10:04:05.123Z", Status = "running" };
        Assert.Equal("2026-11-25 10:04:05 down running       0 startup", CliDatabasesCommandProcessor.FormatRunLine(run));
    }

    [Fact]
    public async Task OnAProtectedServer_NeedsDatabasesRead()
    {
        var h = await UsersTestHost.StartAsync(o => o.MinPasswordLength = 4, configureCli: Databases());
        _hosts.Add(h);
        var registry = h.Service<ICliCommandRegistry>();
        var root = await h.RootTokenAsync();
        var runner = await h.UserWithPermissionsAsync(root, "runner", "commands:execute");
        var reader = await h.UserWithPermissionsAsync(root, "reader", "commands:execute", "databases:read");

        Assert.Equal(401, (await ConfigCommands.RunAsync(h.Client, registry, "databases runs")).Status);
        var denied = await ConfigCommands.RunAsync(h.Client, registry, "databases runs", runner);
        Assert.Equal(403, denied.Status);
        Assert.Contains("databases:read", denied.Body.GetProperty("error").GetString());
        Assert.Equal("main: no migration runs yet", (await ConfigCommands.RunAsync(h.Client, registry, "databases runs", reader)).Text);
    }
}
