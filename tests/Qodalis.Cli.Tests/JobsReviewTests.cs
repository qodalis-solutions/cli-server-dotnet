using Qodalis.Cli.Abstractions.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Plugin.Jobs;
using Qodalis.Cli.Plugin.Jobs.EfCore;
using Qodalis.Cli.Plugin.Audit.EfCore;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests;

/// <summary>A store with only the required members: no cross-job query, prune or delete of its own.</summary>
public sealed class MinimalJobStorage : ICliJobStorageProvider
{
    private readonly InMemoryJobStorageProvider _inner = new();

    /// <summary>Refuses to save an execution that carries properties (as a store whose serializer fails would).</summary>
    public bool RefuseProperties { get; set; }

    public Task SaveExecutionAsync(JobExecution execution, CancellationToken cancellationToken = default) =>
        RefuseProperties && execution.Properties.Count > 0
            ? throw new InvalidOperationException("cannot serialize")
            : _inner.SaveExecutionAsync(execution, cancellationToken);

    public Task<(List<JobExecution> Items, int Total)> GetExecutionsAsync(string jobId, int limit = 20, int offset = 0,
        string? statusFilter = null, CancellationToken cancellationToken = default) =>
        _inner.GetExecutionsAsync(jobId, limit, offset, statusFilter, cancellationToken);

    public Task<JobExecution?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default) =>
        _inner.GetExecutionAsync(executionId, cancellationToken);

    public Task SaveJobStateAsync(string jobId, JobState state, CancellationToken cancellationToken = default) =>
        _inner.SaveJobStateAsync(jobId, state, cancellationToken);

    public Task<JobState?> GetJobStateAsync(string jobId, CancellationToken cancellationToken = default) =>
        _inner.GetJobStateAsync(jobId, cancellationToken);

    public Task<Dictionary<string, JobState>> GetAllJobStatesAsync(CancellationToken cancellationToken = default) =>
        _inner.GetAllJobStatesAsync(cancellationToken);
}

public class JobsReviewTests : IDisposable
{
    private readonly MinimalJobStorage _storage = new();
    private readonly CliJobScheduler _scheduler;

    public JobsReviewTests()
    {
        _scheduler = new CliJobScheduler(_storage, new Mock<ICliEventSocketManager>().Object, new Mock<ILogger<CliJobScheduler>>().Object);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        GC.SuppressFinalize(this);
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        for (var i = 0; i < 50; i++)
        {
            var value = await read();
            if (done(value)) return value;
            await Task.Delay(20);
        }
        return await read();
    }

    [Fact]
    public async Task AJobTheCodeDisables_StaysStopped_WhateverWasSaved()
    {
        await _storage.SaveJobStateAsync("nightly", new JobState { Status = JobStatus.Active, LastRunAt = DateTime.UtcNow.AddHours(-2) });
        await _storage.SaveJobStateAsync("weekly", new JobState { Status = JobStatus.Stopped });
        var nightly = new DummyJob();
        _scheduler.Register(nightly, new CliJobOptions { Name = "nightly", Interval = TimeSpan.FromHours(1), Enabled = false });
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "weekly", Interval = TimeSpan.FromHours(1) });

        await _scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        Assert.Equal(JobStatus.Stopped, _scheduler.Jobs["nightly"].Status);
        Assert.Equal(0, nightly.CallCount);
        Assert.NotNull(_scheduler.Jobs["nightly"].LastRunAt);
        // A stop made at run time is kept
        Assert.Equal(JobStatus.Stopped, _scheduler.Jobs["weekly"].Status);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecutionsOfEveryJob_WorkWithAStoreWithoutItsOwnQuery()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Id = "a" });
        _scheduler.Register(new DummyJob(), new CliJobOptions { Id = "b", Group = "night" });
        var controller = new CliJobsController(new SchedulerJobManager(_scheduler, _storage));
        await _scheduler.TriggerAsync("a");
        await _scheduler.TriggerAsync("b");
        await EventuallyAsync(() => _storage.GetExecutionsAsync("b"), r => r.Items.Any(e => e.Status == JobExecutionStatus.Completed));

        var all = Assert.IsType<OkObjectResult>(await controller.GetExecutions());
        var both = Assert.IsType<OkObjectResult>(await controller.GetExecutions(job: ["a,b"]));
        var night = Assert.IsType<OkObjectResult>(await controller.GetExecutions(group: "NIGHT"));
        var empty = Assert.IsType<OkObjectResult>(await controller.GetExecutions(group: "nobody"));

        Assert.Equal(2, Total(all));
        Assert.Equal(2, Total(both));
        Assert.Equal(1, Total(night));
        Assert.Equal(0, Total(empty));
    }

    [Fact]
    public async Task AManagerWithoutACrossJobQuery_Answers501()
    {
        var manager = new Mock<ICliJobManager>();
        manager.Setup(m => m.QueryExecutionsAsync(It.IsAny<JobExecutionQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("no"));
        var controller = new CliJobsController(manager.Object);

        var result = Assert.IsType<ObjectResult>(await controller.GetExecutions());

        Assert.Equal(501, result.StatusCode);
    }

    [Fact]
    public async Task ARunWhosePropertiesCannotBeSaved_IsSavedWithoutThem()
    {
        _storage.RefuseProperties = true;
        _scheduler.Register(new DummyJob(), new CliJobOptions { Id = "props" });

        await _scheduler.TriggerAsync("props");
        var (items, _) = await EventuallyAsync(() => _storage.GetExecutionsAsync("props"),
            r => r.Items.Any(e => e.Status != JobExecutionStatus.Running));

        var execution = Assert.Single(items);
        Assert.Equal(JobExecutionStatus.Completed, execution.Status);
        Assert.Empty(execution.Properties);
        Assert.Contains(execution.Logs, l => l.Level == "error" && l.Message.Contains("properties"));
        Assert.Null(_scheduler.Jobs["props"].CurrentExecutionId);
    }

    [Fact]
    public void JobIds_AreUrlSafe()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "reports/daily" });

        Assert.Equal(["reports-daily"], _scheduler.Jobs.Keys);
        Assert.Throws<ArgumentException>(() => _scheduler.Register(new DummyJob(), new CliJobOptions { Id = "a/b" }));
    }

    [Fact]
    public void ExecutionQuery_ReadsATimeWithoutAKindAsUtc()
    {
        var unspecified = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(DateTimeKind.Utc, JobExecutionQuery.AsUtc(unspecified).Kind);
        Assert.Equal(unspecified.Ticks, JobExecutionQuery.AsUtc(unspecified).Ticks);
    }

    [Fact]
    public async Task Prune_TakesAnOffset()
    {
        ICliJobStorageProvider storage = new InMemoryJobStorageProvider();
        var started = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);
        await storage.SaveExecutionAsync(new JobExecution { JobId = "a", JobName = "a", StartedAt = started });

        // 11:30 at +02:00 is 09:30 UTC: before the run, so nothing is pruned
        Assert.Equal(0, await storage.PruneAsync(new DateTimeOffset(2026, 11, 5, 11, 30, 0, TimeSpan.FromHours(2))));
        Assert.Equal(1, await storage.PruneAsync(new DateTimeOffset(2026, 11, 5, 12, 30, 0, TimeSpan.FromHours(2))));
    }

    private static int Total(OkObjectResult result) =>
        (int)result.Value!.GetType().GetProperty("total")!.GetValue(result.Value)!;
}

/// <summary>The EF Core stores set up their tables once, even when the first calls come together.</summary>
public sealed class EfCoreStoresFirstUseTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"qcli-first-use-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private sealed class Factory<T>(DbContextOptions<T> options, Func<DbContextOptions<T>, T> create)
        : IDbContextFactory<T> where T : DbContext
    {
        public T CreateDbContext() => create(options);
    }

    [Fact]
    public async Task ConcurrentFirstCalls_CreateTheTablesOnce()
    {
        // A host's database that already has a table of its own
        using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE host_things (id INTEGER PRIMARY KEY)";
            command.ExecuteNonQuery();
        }

        var audit = new EfCoreAuditStore(new Factory<AuditStorageDbContext>(
            new DbContextOptionsBuilder<AuditStorageDbContext>().UseSqlite($"Data Source={_path}").Options, o => new AuditStorageDbContext(o)));
        var jobs = new EfCoreJobStorageProvider(new Factory<JobStorageDbContext>(
            new DbContextOptionsBuilder<JobStorageDbContext>().UseSqlite($"Data Source={_path}").Options, o => new JobStorageDbContext(o)));

        // Reads, so SQLite's single writer does not get in the way: each first call sets the tables up
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => audit.GetEntriesAsync())));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => jobs.GetAllJobStatesAsync())));

        await audit.LogAsync(new AuditEntry { Event = "login_failed", Username = "u", Timestamp = 1 });
        await jobs.SaveJobStateAsync("job", new JobState { Status = JobStatus.Active });
        Assert.Single(await audit.GetEntriesAsync());
        Assert.Single(await jobs.GetAllJobStatesAsync());
    }
}
