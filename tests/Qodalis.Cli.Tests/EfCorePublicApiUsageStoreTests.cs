using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Qodalis.Cli.Plugin.PublicApi;
using Qodalis.Cli.Plugin.PublicApi.EfCore;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>Counts the <c>CREATE TABLE</c> commands a context sends.</summary>
internal sealed class CreateTableCounter : DbCommandInterceptor
{
    public int Creates;

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Count(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Count(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    private void Count(DbCommand command)
    {
        if (command.CommandText.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
            Interlocked.Increment(ref Creates);
    }
}

/// <summary>A host's own context whose migrations own the usage table.</summary>
internal sealed class HostUsageDbContext(DbContextOptions<HostUsageDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyPublicApiUsageModel();
}

/// <summary>Task 5: usage counters in a database (<c>qcli_api_usage</c>), shared by every process.</summary>
public class EfCorePublicApiUsageStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly EfCorePublicApiUsageStore _store;
    private static readonly CancellationToken None = CancellationToken.None;

    public EfCorePublicApiUsageStoreTests()
    {
        _connection.Open();
        _store = Store(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static EfCorePublicApiUsageStore Store(DbConnection connection, EfCorePublicApiUsageStoreOptions? options = null,
        IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<PublicApiUsageDbContext>().UseSqlite(connection);
        if (interceptor != null) builder.AddInterceptors(interceptor);
        return new EfCorePublicApiUsageStore(
            new SqliteContextFactory<PublicApiUsageDbContext>(builder.Options, o => new PublicApiUsageDbContext(o)), options);
    }

    [Fact]
    public async Task Increment_InsertsThenUpdates()
    {
        Assert.Equal(1, await _store.IncrementAsync("t1", "u1", "2026-11-19", None));
        Assert.Equal(2, await _store.IncrementAsync("t1", "u1", "2026-11-19", None));

        Assert.Equal(2, await _store.GetAccountCountAsync("u1", "2026-11-19", None));
    }

    [Fact]
    public async Task AccountCount_SumsTokens()
    {
        for (var i = 0; i < 2; i++) await _store.IncrementAsync("t1", "u1", "2026-11-19", None);
        for (var i = 0; i < 3; i++) await _store.IncrementAsync("t2", "u1", "2026-11-19", None);
        await _store.IncrementAsync("t3", "u2", "2026-11-19", None);
        await _store.IncrementAsync("t1", "u1", "2026-11-18", None);

        Assert.Equal(5, await _store.GetAccountCountAsync("u1", "2026-11-19", None));
        Assert.Equal(1, await _store.GetAccountCountAsync("u2", "2026-11-19", None));
        Assert.Equal(0, await _store.GetAccountCountAsync("u3", "2026-11-19", None));
    }

    [Fact]
    public async Task Decrement_Compensates()
    {
        await _store.IncrementAsync("t1", "u1", "2026-11-19T23:59", None);
        await _store.IncrementAsync("t1", "u1", "2026-11-19T23:59", None);

        await _store.DecrementAsync("t1", "2026-11-19T23:59", None);
        Assert.Equal(1, await _store.GetAccountCountAsync("u1", "2026-11-19T23:59", None));
        await _store.DecrementAsync("t1", "2026-11-19T23:59", None);
        await _store.DecrementAsync("t1", "2026-11-19T23:59", None);
        await _store.DecrementAsync("missing", "2026-11-19T23:59", None);
        Assert.Equal(0, await _store.GetAccountCountAsync("u1", "2026-11-19T23:59", None));
    }

    [Fact]
    public async Task TokenDays_ReturnsRange()
    {
        foreach (var day in new[] { "2026-11-17", "2026-11-18", "2026-11-19" })
            await _store.IncrementAsync("t1", "u1", day, None);
        await _store.IncrementAsync("t1", "u1", "2026-11-19", None);
        await _store.IncrementAsync("t1", "u1", "2026-11-19T10:00", None);
        await _store.IncrementAsync("t2", "u1", "2026-11-19", None);

        var days = await _store.GetTokenDaysAsync(["t1"], "2026-11-18", "2026-11-19", None);

        var t1 = Assert.Single(days).Value;
        Assert.Equal(new Dictionary<string, long> { ["2026-11-18"] = 1, ["2026-11-19"] = 2 }, t1);
    }

    [Fact]
    public async Task Prune_RemovesOldDaysAndMinutes()
    {
        await _store.IncrementAsync("t1", "u1", "2026-10-01", None);
        await _store.IncrementAsync("t1", "u1", "2026-11-19", None);
        await _store.IncrementAsync("t1", "u1", "2026-11-19T10:00", None);
        await _store.IncrementAsync("t1", "u1", "2026-11-19T10:05", None);

        await _store.PruneAsync("2026-10-19", "2026-11-19T10:03", None);

        Assert.Equal(0, await _store.GetAccountCountAsync("u1", "2026-10-01", None));
        Assert.Equal(1, await _store.GetAccountCountAsync("u1", "2026-11-19", None));
        Assert.Equal(0, await _store.GetAccountCountAsync("u1", "2026-11-19T10:00", None));
        Assert.Equal(1, await _store.GetAccountCountAsync("u1", "2026-11-19T10:05", None));
    }

    [Fact]
    public async Task TwoStores_ShareCounters_NeverExceed()
    {
        var file = Path.Combine(Path.GetTempPath(), $"qcli-usage-{Guid.NewGuid():N}.db");
        var users = new InMemoryUserStore();
        var groups = new InMemoryGroupStore();
        var tokens = new InMemoryApiTokenStore();
        try
        {
            var limit = new Action<PublicApiOptions>(o => o.UseQuotaResolver((_, _, _) => ValueTask.FromResult(new PublicApiQuota(20, null))));
            // One connection per context (pooled), as with a real database.
            EfCorePublicApiUsageStore FileStore() => new(new SqliteContextFactory<PublicApiUsageDbContext>(
                new DbContextOptionsBuilder<PublicApiUsageDbContext>().UseSqlite($"Data Source={file};Default Timeout=30").Options,
                o => new PublicApiUsageDbContext(o)));

            await using var first = await PublicApiTestHost.StartAsync(o => { limit(o); o.SetUsageStore(_ => FileStore()); },
                configureUsers: o => { o.UserStore = users; o.GroupStore = groups; o.ApiTokenStore = tokens; });
            await using var second = await PublicApiTestHost.StartAsync(o => { limit(o); o.SetUsageStore(_ => FileStore()); },
                configureUsers: o => { o.UserStore = users; o.GroupStore = groups; o.ApiTokenStore = tokens; });
            var (aliceId, token) = await first.HostTokenAsync("alice");

            var results = await Task.WhenAll(Enumerable.Range(0, 60)
                .Select(i => (i % 2 == 0 ? first : second).GetAsync("/v1/rates", token)));

            var admitted = results.Count(r => (int)r.StatusCode == 200);
            Assert.InRange(admitted, 18, 20);
            Assert.All(results, r => Assert.Contains((int)r.StatusCode, new[] { 200, 429 }));
            Assert.Equal(admitted, await first.Service<IPublicApiUsageStore>().GetAccountCountAsync(aliceId,
                PublicApiTestHost.LateEvening.ToString("yyyy-MM-dd"), None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public async Task HostOwnedTable()
    {
        await using (var host = new HostUsageDbContext(new DbContextOptionsBuilder<HostUsageDbContext>().UseSqlite(_connection).Options))
            await host.Database.EnsureCreatedAsync();
        var counter = new CreateTableCounter();
        var store = Store(_connection, new EfCorePublicApiUsageStoreOptions { EnsureCreated = false }, counter);

        Assert.Equal(1, await store.IncrementAsync("t1", "u1", "2026-11-19", None));
        Assert.Equal(1, await store.GetAccountCountAsync("u1", "2026-11-19", None));

        Assert.Equal(0, counter.Creates);
    }

    [Fact]
    public async Task CreatesItsTableNextToTheHostsTables()
    {
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE host_things (id INTEGER PRIMARY KEY)";
            command.ExecuteNonQuery();
        }
        var store = Store(_connection);

        Assert.Equal(1, await store.IncrementAsync("t1", "u1", "2026-11-19", None));
    }
}
