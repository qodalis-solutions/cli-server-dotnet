using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.Jobs.EfCore;
using Qodalis.Cli.Plugin.Users.EfCore;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Tests;

/// <summary>A context factory over one open SQLite connection (an in-memory database lives as long as it).</summary>
internal sealed class SqliteContextFactory<T>(DbContextOptions<T> options, Func<DbContextOptions<T>, T> create)
    : IDbContextFactory<T> where T : DbContext
{
    public T CreateDbContext() => create(options);
}

public class EfCoreAuditStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly EfCoreAuditStore _store;

    public EfCoreAuditStoreTests()
    {
        _connection.Open();
        // A host's own table: the database exists, so the store must add its table next to it
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE host_things (id INTEGER PRIMARY KEY)";
            command.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<AuditStorageDbContext>().UseSqlite(_connection).Options;
        _store = new EfCoreAuditStore(new SqliteContextFactory<AuditStorageDbContext>(options, o => new AuditStorageDbContext(o)));
    }

    public void Dispose() => _connection.Dispose();

    private static AuditEntry Entry(string @event, long timestamp, string username = "bob", string? actor = null) => new()
    {
        Timestamp = timestamp,
        Event = @event,
        Username = username,
        Actor = actor,
    };

    [Fact]
    public async Task Log_CreatesTheTableInAnExistingDatabase_AndRoundTrips()
    {
        await _store.LogAsync(new AuditEntry
        {
            Timestamp = 1000,
            Event = "role.save",
            Username = "",
            Actor = "admin@example.com",
            ActorId = "a1",
            Target = "role:editor",
            Details = "{\"permissions\":[\"jobs.view\"]}",
            Properties = new() { ["before"] = "viewer", ["count"] = 2 },
            Metadata = new AuditMetadata { RemoteAddress = "10.0.0.1", Mode = "command" },
        });

        var entry = Assert.Single(await _store.GetEntriesAsync());
        Assert.False(string.IsNullOrEmpty(entry.Id));
        Assert.Equal("role.save", entry.Event);
        Assert.Equal("role:editor", entry.Target);
        Assert.Equal("admin@example.com", entry.Actor);
        Assert.Equal("10.0.0.1", entry.Metadata?.RemoteAddress);
        Assert.Equal("command", entry.Metadata?.Mode);
        Assert.Equal("viewer", entry.Properties!["before"]!.ToString());
        Assert.Equal(2, ((System.Text.Json.JsonElement)entry.Properties["count"]!).GetInt32());
    }

    [Fact]
    public async Task GetEntries_FiltersCaseInsensitively_NewestFirst_AndPages()
    {
        await _store.LogAsync(Entry("login", 1, actor: "Alice@Example.com"));
        await _store.LogAsync(Entry("login", 3, actor: "alice@example.com"));
        await _store.LogAsync(Entry("logout", 2, username: "carol", actor: "alice@example.com"));
        await _store.LogAsync(Entry("login", 3, actor: "dave@example.com"));

        var all = await _store.GetEntriesAsync();
        Assert.Equal([3, 3, 2, 1], all.Select(e => e.Timestamp));
        // Same timestamp: the later entry first
        Assert.Equal("dave@example.com", all[0].Actor);

        Assert.Equal(3, (await _store.GetEntriesAsync(new AuditQueryOptions { Actor = "ALICE@example.com" })).Count);
        Assert.Single(await _store.GetEntriesAsync(new AuditQueryOptions { Username = "Carol" }));
        Assert.Equal(3, (await _store.GetEntriesAsync(new AuditQueryOptions { Event = "LOGIN" })).Count);

        var page = await _store.GetEntriesAsync(new AuditQueryOptions { Offset = 1, Limit = 2 });
        Assert.Equal([3, 2], page.Select(e => e.Timestamp));
    }

    [Fact]
    public async Task Prune_DeletesOlderEntries_AndClearDeletesAll()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.LogAsync(Entry("old", now.AddDays(-400).ToUnixTimeMilliseconds()));
        await _store.LogAsync(Entry("new", now.ToUnixTimeMilliseconds()));

        Assert.Equal(1, await _store.PruneAsync(now.AddDays(-365)));
        Assert.Equal("new", Assert.Single(await _store.GetEntriesAsync()).Event);

        await _store.ClearAsync();
        Assert.Empty(await _store.GetEntriesAsync());
    }

    [Fact]
    public void AddEfCoreAuditStore_ReplacesTheInMemoryStore_InEitherOrder_AndIsListed()
    {
        foreach (var efFirst in new[] { true, false })
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddControllers().AddCli(cli =>
            {
                if (efFirst) cli.AddEfCoreAuditStore(o => o.UseSqlite(_connection));
                cli.AddUsers();
                if (!efFirst) cli.AddEfCoreAuditStore(o => o.UseSqlite(_connection));
            });
            var sp = services.BuildServiceProvider();

            Assert.IsType<EfCoreAuditStore>(sp.GetRequiredService<IAuditStore>());
            Assert.Contains(sp.GetRequiredService<CliInstalledPlugins>().All, p => p.Id == "Qodalis.Cli.Plugin.Users.EfCore");
        }
    }
}

public class EfCoreJobStorageSqliteTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly EfCoreJobStorageProvider _provider;

    public EfCoreJobStorageSqliteTests()
    {
        _connection.Open();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE host_things (id INTEGER PRIMARY KEY)";
            command.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<JobStorageDbContext>().UseSqlite(_connection).Options;
        _provider = new EfCoreJobStorageProvider(new SqliteContextFactory<JobStorageDbContext>(options, o => new JobStorageDbContext(o)));
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task CreatesItsTablesInAnExistingDatabase_AndPrunesOldExecutionsWithTheirLogs()
    {
        var now = DateTime.UtcNow;
        var old = new JobExecution
        {
            JobId = "fetch", JobName = "fetch", Status = JobExecutionStatus.Completed, StartedAt = now.AddDays(-40),
            Logs = [new JobLogEntry { Level = "info", Message = "old" }],
        };
        var recent = new JobExecution
        {
            JobId = "fetch", JobName = "fetch", Status = JobExecutionStatus.Completed, StartedAt = now,
            Properties = new() { ["items"] = 32 },
        };
        await _provider.SaveExecutionAsync(old);
        await _provider.SaveExecutionAsync(recent);

        Assert.Equal(1, await _provider.PruneAsync(now.AddDays(-30)));

        var (items, total) = await _provider.GetExecutionsAsync("fetch");
        Assert.Equal(1, total);
        Assert.Equal(recent.Id, items[0].Id);
        Assert.Equal(32, ((System.Text.Json.JsonElement)items[0].Properties["items"]!).GetInt32());
        Assert.Null(await _provider.GetExecutionAsync(old.Id));
    }

    [Fact]
    public async Task DeleteExecution_RemovesItWithItsLogs()
    {
        var execution = new JobExecution
        {
            JobId = "digest", JobName = "digest", Status = JobExecutionStatus.Completed,
            Logs = [new JobLogEntry { Level = "info", Message = "nothing due" }],
        };
        await _provider.SaveExecutionAsync(execution);

        Assert.True(await _provider.DeleteExecutionAsync(execution.Id));
        Assert.False(await _provider.DeleteExecutionAsync(execution.Id));
        Assert.Null(await _provider.GetExecutionAsync(execution.Id));
    }

    [Fact]
    public async Task QueryExecutions_AcrossJobs_FiltersAndPages()
    {
        var now = DateTime.UtcNow;
        foreach (var (job, minutes, status) in new[] { ("a", 1, JobExecutionStatus.Completed), ("b", 2, JobExecutionStatus.Failed), ("c", 3, JobExecutionStatus.Completed) })
            await _provider.SaveExecutionAsync(new JobExecution { JobId = job, JobName = job, Status = status, StartedAt = now.AddMinutes(minutes) });

        var (all, total) = await _provider.QueryExecutionsAsync(new JobExecutionQuery());
        Assert.Equal(3, total);
        Assert.Equal(["c", "b", "a"], all.Select(e => e.JobId));

        var (some, _) = await _provider.QueryExecutionsAsync(new JobExecutionQuery { JobIds = ["a", "b"], Limit = 1, Offset = 1 });
        Assert.Equal("a", Assert.Single(some).JobId);

        var (failed, failedTotal) = await _provider.QueryExecutionsAsync(new JobExecutionQuery { Status = "FAILED" });
        Assert.Equal(1, failedTotal);
        Assert.Equal("b", failed[0].JobId);

        var (recent, recentTotal) = await _provider.QueryExecutionsAsync(new JobExecutionQuery { Since = now.AddMinutes(2) });
        Assert.Equal(2, recentTotal);
        Assert.Equal(["c", "b"], recent.Select(e => e.JobId));
    }

    [Fact]
    public void AddEfCoreJobStorage_BeforeTheFirstJob_IsNotReplacedByTheInMemoryDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddCli(cli =>
        {
            cli.AddEfCoreJobStorage(o => o.UseSqlite(_connection));
            Qodalis.Cli.Plugin.Jobs.CliBuilderJobExtensions.AddJob(cli, new DummyJob());
        });
        var sp = services.BuildServiceProvider();

        Assert.IsType<EfCoreJobStorageProvider>(sp.GetRequiredService<ICliJobStorageProvider>());
    }
}
