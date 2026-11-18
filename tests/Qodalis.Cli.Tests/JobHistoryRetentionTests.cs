using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Jobs;
using Qodalis.Cli.Plugin.Jobs.EfCore;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests;

/// <summary>History retention in a store: the same cases for every store.</summary>
public abstract class JobHistoryRetentionStoreTests
{
    protected abstract ICliJobStorageProvider Store { get; }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    /// <summary>Saves a run of job <paramref name="jobId"/> that started <paramref name="hoursAgo"/> hours ago.</summary>
    protected async Task<JobExecution> RunAsync(string jobId, double hoursAgo,
        JobExecutionStatus status = JobExecutionStatus.Completed)
    {
        var execution = new JobExecution
        {
            JobId = jobId,
            JobName = jobId,
            Status = status,
            StartedAt = Now.UtcDateTime.AddHours(-hoursAgo),
            Logs = [new JobLogEntry { Timestamp = Now.UtcDateTime.AddHours(-hoursAgo), Level = "info", Message = "hi" }],
        };
        await Store.SaveExecutionAsync(execution);
        return execution;
    }

    private async Task<List<string>> IdsAsync(string jobId) =>
        (await Store.GetExecutionsAsync(jobId, 1000)).Items.Select(e => e.Id).ToList();

    [Fact]
    public async Task AgeLimit_DeletesOlderRuns()
    {
        var fresh = await RunAsync("a", 1);
        var old1 = await RunAsync("a", 48);
        var old2 = await RunAsync("a", 72);

        var deleted = await Store.PruneAsync("a", new JobHistoryRetention { MaxAge = TimeSpan.FromDays(1) }, Now);

        Assert.Equal(2, deleted);
        Assert.Equal([fresh.Id], await IdsAsync("a"));
        Assert.Null(await Store.GetExecutionAsync(old1.Id));
        Assert.Null(await Store.GetExecutionAsync(old2.Id));
    }

    [Fact]
    public async Task CountLimit_KeepsTheNewestRuns()
    {
        var runs = new List<JobExecution>();
        for (var i = 0; i < 6; i++) runs.Add(await RunAsync("a", i + 1));
        var other = await RunAsync("b", 100);

        var deleted = await Store.PruneAsync("a", new JobHistoryRetention { MaxRunsPerJob = 2 }, Now);

        Assert.Equal(4, deleted);
        Assert.Equal([runs[0].Id, runs[1].Id], await IdsAsync("a"));
        Assert.Equal([other.Id], await IdsAsync("b"));
    }

    [Fact]
    public async Task FailedLimits_KeepFailedRunsLonger()
    {
        var newest = await RunAsync("a", 1);
        var oldFailed = await RunAsync("a", 48, JobExecutionStatus.Failed);
        var oldCompleted = await RunAsync("a", 49);
        var veryOldFailed = await RunAsync("a", 24 * 10, JobExecutionStatus.Failed);

        var deleted = await Store.PruneAsync("a", new JobHistoryRetention
        {
            MaxAge = TimeSpan.FromDays(1),
            FailedMaxAge = TimeSpan.FromDays(7),
        }, Now);

        Assert.Equal(2, deleted);
        Assert.Equal([newest.Id, oldFailed.Id], await IdsAsync("a"));
        Assert.Null(await Store.GetExecutionAsync(oldCompleted.Id));
        Assert.Null(await Store.GetExecutionAsync(veryOldFailed.Id));
    }

    [Fact]
    public async Task FailedCountLimit_CountsFailedRunsOnTheirOwn()
    {
        var runs = new List<JobExecution>();
        for (var i = 0; i < 8; i++)
            runs.Add(await RunAsync("a", i + 1, i % 2 == 0 ? JobExecutionStatus.Completed : JobExecutionStatus.Failed));

        // The newest 2 non-failed runs (0, 2) and the newest 3 failed ones (1, 3, 5)
        var deleted = await Store.PruneAsync("a", new JobHistoryRetention { MaxRunsPerJob = 2, FailedMaxRunsPerJob = 3 }, Now);

        Assert.Equal(3, deleted);
        Assert.Equal([runs[0].Id, runs[1].Id, runs[2].Id, runs[3].Id, runs[5].Id], await IdsAsync("a"));
    }

    [Fact]
    public async Task FailedCountLimitOnly_LeavesOtherRunsUncounted()
    {
        var runs = new List<JobExecution>();
        for (var i = 0; i < 6; i++)
            runs.Add(await RunAsync("a", i + 1, i < 3 ? JobExecutionStatus.Completed : JobExecutionStatus.Failed));

        var deleted = await Store.PruneAsync("a", new JobHistoryRetention { FailedMaxRunsPerJob = 1 }, Now);

        Assert.Equal(2, deleted);
        Assert.Equal([runs[0].Id, runs[1].Id, runs[2].Id, runs[3].Id], await IdsAsync("a"));
    }

    [Fact]
    public async Task CountLimit_WithoutFailedLimit_RanksFailedRunsWithTheOthers()
    {
        var newest = await RunAsync("a", 1, JobExecutionStatus.Failed);
        var second = await RunAsync("a", 2);
        await RunAsync("a", 3, JobExecutionStatus.Failed);
        await RunAsync("a", 4);

        var deleted = await Store.PruneAsync("a", new JobHistoryRetention { MaxRunsPerJob = 2, FailedMaxAge = TimeSpan.FromDays(9) }, Now);

        Assert.Equal(2, deleted);
        Assert.Equal([newest.Id, second.Id], await IdsAsync("a"));
    }

    [Fact]
    public async Task RunningRuns_TakeTheirPlaceInTheCount()
    {
        var newest = await RunAsync("a", 1);
        var running = await RunAsync("a", 2, JobExecutionStatus.Running);
        await RunAsync("a", 3);
        await RunAsync("a", 4);

        var deleted = await Store.PruneAsync("a", new JobHistoryRetention { MaxRunsPerJob = 2 }, Now);

        Assert.Equal(2, deleted);
        Assert.Equal([newest.Id, running.Id], await IdsAsync("a"));
    }

    [Fact]
    public async Task RunningAndNewestRuns_AreNeverDeleted()
    {
        var newest = await RunAsync("a", 30);
        var running = await RunAsync("a", 40, JobExecutionStatus.Running);
        var old = await RunAsync("a", 50);

        var deleted = await Store.PruneAsync("a", new JobHistoryRetention { MaxAge = TimeSpan.FromHours(1), MaxRunsPerJob = 1 }, Now);

        Assert.Equal(1, deleted);
        Assert.Equal([newest.Id, running.Id], await IdsAsync("a"));
        Assert.Null(await Store.GetExecutionAsync(old.Id));
    }

    [Fact]
    public async Task NoLimits_DeletesNothing()
    {
        await RunAsync("a", 1000);
        await RunAsync("a", 2000);

        Assert.Equal(0, await Store.PruneAsync("a", new JobHistoryRetention(), Now));
        Assert.Equal(2, (await IdsAsync("a")).Count);
    }
}

public class InMemoryJobHistoryRetentionTests : JobHistoryRetentionStoreTests
{
    protected override ICliJobStorageProvider Store { get; } = new InMemoryJobStorageProvider();
}

/// <summary>The interface's default prune (a store without its own), over the in-memory store's other methods.</summary>
public class DefaultJobHistoryRetentionTests : JobHistoryRetentionStoreTests
{
    protected override ICliJobStorageProvider Store { get; } = new PlainStore(new InMemoryJobStorageProvider());

    private sealed class PlainStore(InMemoryJobStorageProvider inner) : ICliJobStorageProvider
    {
        public Task SaveExecutionAsync(JobExecution execution, CancellationToken cancellationToken = default) =>
            inner.SaveExecutionAsync(execution, cancellationToken);

        public Task<(List<JobExecution> Items, int Total)> GetExecutionsAsync(string jobId, int limit = 20, int offset = 0,
            string? statusFilter = null, CancellationToken cancellationToken = default) =>
            inner.GetExecutionsAsync(jobId, limit, offset, statusFilter, cancellationToken);

        public Task<bool> DeleteExecutionAsync(string executionId, CancellationToken cancellationToken = default) =>
            inner.DeleteExecutionAsync(executionId, cancellationToken);

        public Task<JobExecution?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default) =>
            inner.GetExecutionAsync(executionId, cancellationToken);

        public Task SaveJobStateAsync(string jobId, JobState state, CancellationToken cancellationToken = default) =>
            inner.SaveJobStateAsync(jobId, state, cancellationToken);

        public Task<JobState?> GetJobStateAsync(string jobId, CancellationToken cancellationToken = default) =>
            inner.GetJobStateAsync(jobId, cancellationToken);

        public Task<Dictionary<string, JobState>> GetAllJobStatesAsync(CancellationToken cancellationToken = default) =>
            inner.GetAllJobStatesAsync(cancellationToken);
    }
}

public class EfCoreJobHistoryRetentionTests : JobHistoryRetentionStoreTests, IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<JobStorageDbContext> _options;

    public EfCoreJobHistoryRetentionTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<JobStorageDbContext>().UseSqlite(_connection).Options;
        Store = new EfCoreJobStorageProvider(
            new SqliteContextFactory<JobStorageDbContext>(_options, o => new JobStorageDbContext(o)));
    }

    protected override ICliJobStorageProvider Store { get; }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private long Count(string table)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task Prune_DeletesTheRunsLogEntries()
    {
        await RunAsync("a", 1);
        for (var i = 0; i < 4; i++) await RunAsync("a", 48 + i);
        Assert.Equal(5, Count("job_log_entries"));

        Assert.Equal(4, await Store.PruneAsync("a", new JobHistoryRetention { MaxRunsPerJob = 3, MaxAge = TimeSpan.FromDays(1) }, DateTimeOffset.UtcNow));

        Assert.Equal(1, Count("job_executions"));
        Assert.Equal(1, Count("job_log_entries"));
    }

    [Fact]
    public async Task CountLimit_SameStartTimes_KeepsExactlyN()
    {
        var at = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < 5; i++)
            await Store.SaveExecutionAsync(new JobExecution { JobId = "a", JobName = "a", Status = JobExecutionStatus.Completed, StartedAt = at });

        Assert.Equal(3, await Store.PruneAsync("a", new JobHistoryRetention { MaxRunsPerJob = 2 }, DateTimeOffset.UtcNow));
        Assert.Equal(2, Count("job_executions"));
    }
}

/// <summary>The scheduler's side: options, per-job overrides, the <c>jobs:prune-history</c> job and the API field.</summary>
public class JobHistoryRetentionSchedulerTests : IDisposable
{
    private readonly InMemoryJobStorageProvider _storage = new();
    private readonly CliJobScheduler _scheduler;

    public JobHistoryRetentionSchedulerTests()
    {
        _scheduler = new CliJobScheduler(_storage, new Mock<ICliEventSocketManager>().Object,
            new Mock<ILogger<CliJobScheduler>>().Object);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedAsync(string jobId, int count, double hoursApart = 1)
    {
        for (var i = 0; i < count; i++)
        {
            await _storage.SaveExecutionAsync(new JobExecution
            {
                JobId = jobId,
                JobName = jobId,
                Status = JobExecutionStatus.Completed,
                StartedAt = DateTime.UtcNow.AddHours(-hoursApart * (i + 1)),
            });
        }
    }

    private async Task<int> CountAsync(string jobId) => (await _storage.GetExecutionsAsync(jobId)).Total;

    [Fact]
    public void NoRetention_NoPruneJob()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "a", Interval = TimeSpan.FromHours(1) });
        _scheduler.ConfigureHistory(new CliJobsHistoryOptions());

        Assert.False(_scheduler.Jobs.ContainsKey(PruneHistoryJob.JobId));
    }

    [Fact]
    public void GlobalRetention_RegistersThePruneJob()
    {
        _scheduler.ConfigureHistory(new CliJobsHistoryOptions { MaxAge = TimeSpan.FromDays(30), PruneInterval = TimeSpan.FromHours(6) });

        var reg = _scheduler.Jobs[PruneHistoryJob.JobId];
        Assert.Equal(PruneHistoryJob.JobId, reg.Options.Name);
        Assert.Equal("system", reg.Options.Group);
        Assert.Contains("system", reg.Options.Tags);
        Assert.Equal(TimeSpan.FromHours(6), reg.Options.Interval);
    }

    [Fact]
    public void PerJobRetentionOnly_RegistersThePruneJob_KeepAllAloneDoesNot()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "keep", History = { KeepAll = true } });
        _scheduler.ConfigureHistory(new CliJobsHistoryOptions());
        Assert.False(_scheduler.Jobs.ContainsKey(PruneHistoryJob.JobId));

        using var other = new CliJobScheduler(_storage, new Mock<ICliEventSocketManager>().Object,
            new Mock<ILogger<CliJobScheduler>>().Object);
        other.Register(new DummyJob(), new CliJobOptions { Name = "noisy", History = { MaxRunsPerJob = 5 } });
        other.ConfigureHistory(new CliJobsHistoryOptions());
        Assert.True(other.Jobs.ContainsKey(PruneHistoryJob.JobId));
    }

    [Fact]
    public async Task PerJobOverride_AndKeepAll()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "global" });
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "noisy", History = { MaxRunsPerJob = 2 } });
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "report", History = { KeepAll = true } });
        _scheduler.ConfigureHistory(new CliJobsHistoryOptions { MaxRunsPerJob = 5 });
        await SeedAsync("global", 10);
        await SeedAsync("noisy", 10);
        await SeedAsync("report", 10);

        var deleted = await _scheduler.PruneHistoryAsync(CancellationToken.None);

        Assert.Equal(5 + 8, deleted);
        Assert.Equal(5, await CountAsync("global"));
        Assert.Equal(2, await CountAsync("noisy"));
        Assert.Equal(10, await CountAsync("report"));
    }

    [Fact]
    public async Task PerJobField_ReplacesOnlyThatGlobalField()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "a", History = { MaxRunsPerJob = 100 } });
        _scheduler.ConfigureHistory(new CliJobsHistoryOptions { MaxAge = TimeSpan.FromHours(5), MaxRunsPerJob = 1 });
        await SeedAsync("a", 10);

        var retention = _scheduler.GetHistoryRetention(_scheduler.Jobs["a"])!;
        Assert.Equal(TimeSpan.FromHours(5), retention.MaxAge);
        Assert.Equal(100, retention.MaxRunsPerJob);
        Assert.True(retention.Overridden);

        await _scheduler.PruneHistoryAsync(CancellationToken.None);
        Assert.Equal(4, await CountAsync("a"));
    }

    [Fact]
    public async Task Orphans_ArePrunedWithTheGlobalLimits()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "a", History = { MaxRunsPerJob = 8 } });
        _scheduler.ConfigureHistory(new CliJobsHistoryOptions { MaxRunsPerJob = 3 });
        await SeedAsync("a", 10);
        await SeedAsync("removed-job", 10);

        Assert.Equal(2 + 7, await _scheduler.PruneHistoryAsync(CancellationToken.None));
        Assert.Equal(8, await CountAsync("a"));
        Assert.Equal(3, await CountAsync("removed-job"));
    }

    [Fact]
    public async Task Orphans_KeptWithoutGlobalLimits()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "a", History = { MaxRunsPerJob = 2 } });
        _scheduler.ConfigureHistory(new CliJobsHistoryOptions());
        await SeedAsync("removed-job", 5);

        await _scheduler.PruneHistoryAsync(CancellationToken.None);
        Assert.Equal(5, await CountAsync("removed-job"));
    }

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(null, -1, null)]
    [InlineData(null, null, 0)]
    public void InvalidJobLimits_ThrowAtRegistration(int? maxRuns, int? failedMaxRuns, int? maxAgeSeconds)
    {
        var options = new CliJobOptions
        {
            Name = "bad",
            History =
            {
                MaxRunsPerJob = maxRuns,
                FailedMaxRunsPerJob = failedMaxRuns,
                MaxAge = maxAgeSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            },
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => _scheduler.Register(new DummyJob(), options));
    }

    [Fact]
    public void InvalidGlobalLimits_ThrowAtBuild()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _scheduler.ConfigureHistory(new CliJobsHistoryOptions { FailedMaxAge = TimeSpan.FromDays(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _scheduler.ConfigureHistory(new CliJobsHistoryOptions { MaxRunsPerJob = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _scheduler.ConfigureHistory(new CliJobsHistoryOptions { MaxAge = TimeSpan.FromDays(1), PruneInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildScheduler(
            cli => cli.AddJob(new DummyJob(), o => o.Name = "a"),
            new Dictionary<string, string?> { ["Jobs:History:MaxRunsPerJob"] = "0" }));
    }

    [Fact]
    public async Task PruneJob_RecordsTheDeletedCount()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "a" });
        _scheduler.ConfigureHistory(new CliJobsHistoryOptions { MaxRunsPerJob = 3 });
        await SeedAsync("a", 7);

        await _scheduler.TriggerAsync(PruneHistoryJob.JobId);
        JobExecution? run = null;
        for (var i = 0; i < 50 && run?.Status is null or JobExecutionStatus.Running; i++)
        {
            await Task.Delay(20);
            run = (await _storage.GetExecutionsAsync(PruneHistoryJob.JobId)).Items.FirstOrDefault();
        }

        Assert.NotNull(run);
        Assert.Equal(JobExecutionStatus.Completed, run.Status);
        Assert.Equal(4, run.Properties["deleted"]);
        Assert.Contains(run.Logs, l => l.Message == "Deleted 4 execution(s)");
        Assert.Equal(3, await CountAsync("a"));
    }

    [Fact]
    public async Task JobInfo_HasHistoryRetention()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "a" });
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "b", History = { FailedMaxRunsPerJob = 50 } });
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "c", History = { KeepAll = true } });
        _scheduler.ConfigureHistory(new CliJobsHistoryOptions { MaxAge = TimeSpan.FromDays(30) });
        var controller = new CliJobsController(new SchedulerJobManager(_scheduler, _storage));

        JsonElement Retention(string id)
        {
            var result = controller.GetJob(id).GetAwaiter().GetResult();
            var json = JsonSerializer.Serialize(((ObjectResult)result).Value);
            return JsonDocument.Parse(json).RootElement.GetProperty("historyRetention");
        }

        var a = Retention("a");
        Assert.Equal(30L * 24 * 3600 * 1000, a.GetProperty("maxAge").GetInt64());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("maxRunsPerJob").ValueKind);
        Assert.Equal(JsonValueKind.Null, a.GetProperty("failedMaxAge").ValueKind);
        Assert.Equal(JsonValueKind.Null, a.GetProperty("failedMaxRunsPerJob").ValueKind);
        Assert.False(a.GetProperty("overridden").GetBoolean());
        Assert.False(a.GetProperty("keepAll").GetBoolean());

        var b = Retention("b");
        Assert.Equal(50, b.GetProperty("failedMaxRunsPerJob").GetInt32());
        Assert.True(b.GetProperty("overridden").GetBoolean());

        var c = Retention("c");
        Assert.True(c.GetProperty("keepAll").GetBoolean());
        Assert.Equal(JsonValueKind.Null, c.GetProperty("maxAge").ValueKind);

        Assert.Equal(JsonValueKind.Object, Retention(PruneHistoryJob.JobId).ValueKind);
    }

    [Fact]
    public async Task JobInfo_WritesUnsetLimitsAsNull_WhenTheSerializerIgnoresNulls()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "a", History = { MaxRunsPerJob = 3 } });
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "keep", History = { KeepAll = true } });
        _scheduler.ConfigureHistory(new CliJobsHistoryOptions());
        var controller = new CliJobsController(new SchedulerJobManager(_scheduler, _storage));
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        foreach (var id in new[] { "a", "keep" })
        {
            var json = JsonSerializer.Serialize(((ObjectResult)await controller.GetJob(id)).Value, options);
            var retention = JsonDocument.Parse(json).RootElement.GetProperty("historyRetention");
            foreach (var key in new[] { "maxAge", "failedMaxAge", "failedMaxRunsPerJob" })
                Assert.Equal(JsonValueKind.Null, retention.GetProperty(key).ValueKind);
            Assert.True(retention.TryGetProperty("maxRunsPerJob", out _));
            Assert.True(retention.TryGetProperty("overridden", out _));
            Assert.True(retention.TryGetProperty("keepAll", out _));
        }
    }

    [Fact]
    public async Task JobInfo_NoLimits_HistoryRetentionIsNull()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "a" });
        var controller = new CliJobsController(new SchedulerJobManager(_scheduler, _storage));

        var result = await controller.GetJob("a");
        var json = JsonSerializer.Serialize(((ObjectResult)result).Value);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(json).RootElement.GetProperty("historyRetention").ValueKind);
    }

    private static CliJobScheduler BuildScheduler(Action<CliBuilder> configure, Dictionary<string, string?>? config = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (config != null)
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.AddControllers().AddCli(configure);
        return services.BuildServiceProvider().GetRequiredService<CliJobScheduler>();
    }

    [Fact]
    public void ConfigureJobs_WorksBeforeAndAfterAddJob()
    {
        var after = BuildScheduler(cli => cli.AddJob(new DummyJob(), o => o.Name = "a")
            .ConfigureJobs(o => o.History.MaxRunsPerJob = 10));
        var before = BuildScheduler(cli => cli.ConfigureJobs(o => o.History.MaxAge = TimeSpan.FromDays(1))
            .AddJob(new DummyJob(), o => o.Name = "a"));

        Assert.Equal(10, after.GetHistoryRetention(after.Jobs["a"])!.MaxRunsPerJob);
        Assert.True(after.Jobs.ContainsKey(PruneHistoryJob.JobId));
        Assert.Equal(TimeSpan.FromDays(1), before.GetHistoryRetention(before.Jobs["a"])!.MaxAge);
        Assert.True(before.Jobs.ContainsKey(PruneHistoryJob.JobId));
    }

    [Fact]
    public void Configuration_BindsJobsHistory_CodeWins()
    {
        var scheduler = BuildScheduler(
            cli => cli.AddJob(new DummyJob(), o => o.Name = "a").ConfigureJobs(o => o.History.MaxRunsPerJob = 7),
            new Dictionary<string, string?>
            {
                ["Jobs:History:MaxAge"] = "30.00:00:00",
                ["Jobs:History:MaxRunsPerJob"] = "500",
                ["Jobs:History:FailedMaxAge"] = "90.00:00:00",
                ["Jobs:History:FailedMaxRunsPerJob"] = "1000",
                ["Jobs:History:PruneInterval"] = "06:00:00",
            });

        var history = scheduler.History;
        Assert.Equal(TimeSpan.FromDays(30), history.MaxAge);
        Assert.Equal(7, history.MaxRunsPerJob);
        Assert.Equal(TimeSpan.FromDays(90), history.FailedMaxAge);
        Assert.Equal(1000, history.FailedMaxRunsPerJob);
        Assert.Equal(TimeSpan.FromHours(6), scheduler.Jobs[PruneHistoryJob.JobId].Options.Interval);
    }

    [Fact]
    public void Default_NoConfiguration_KeepsEverything()
    {
        var scheduler = BuildScheduler(cli => cli.AddJob(new DummyJob(), o => o.Name = "a"));

        Assert.False(scheduler.History.HasLimits);
        Assert.False(scheduler.Jobs.ContainsKey(PruneHistoryJob.JobId));
        Assert.Null(scheduler.GetHistoryRetention(scheduler.Jobs["a"]));
    }
}
