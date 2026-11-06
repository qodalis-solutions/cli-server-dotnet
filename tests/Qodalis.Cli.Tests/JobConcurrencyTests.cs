using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Plugin.Jobs;
using Qodalis.Cli.Plugin.Jobs.EfCore;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests;

#region Helpers

/// <summary>
/// A clock whose time moves only when told to, with timers that fire only on <see cref="Advance"/> (when due) or
/// <see cref="FireAll"/> (early, as a real timer may).
/// </summary>
internal sealed class JobTestClock : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;

    public JobTestClock(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) return _now;
    }

    public void SetUtcNow(DateTimeOffset now)
    {
        lock (_lock) _now = now;
    }

    /// <summary>The timers armed right now.</summary>
    public int ArmedTimers
    {
        get
        {
            lock (_lock) return _timers.Count(t => t.Due != null);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_lock) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock and fires every timer that is due, in order.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset now;
        lock (_lock)
        {
            _now += by;
            now = _now;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_lock)
            {
                next = _timers.Where(t => t.Due <= now).OrderBy(t => t.Due).FirstOrDefault();
                next?.Fired();
            }
            if (next == null) return;
            next.Callback(next.State);
        }
    }

    /// <summary>Fires every armed timer now, whatever its due time (a timer firing early).</summary>
    public void FireAll()
    {
        List<ManualTimer> armed;
        lock (_lock)
        {
            armed = _timers.Where(t => t.Due != null).ToList();
            foreach (var t in armed) t.Fired();
        }
        foreach (var t in armed) t.Callback(t.State);
    }

    private sealed class ManualTimer(JobTestClock owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public DateTimeOffset? Due { get; private set; }
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public void Fired() => Due = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan ? Due + _period : null;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            }
            return true;
        }

        public void Dispose()
        {
            lock (owner._lock)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A job that holds every run until <see cref="Release"/> (or its cancellation), counting runs and the most that ran
/// at the same time. With <c>failFirst</c> its first call fails.
/// </summary>
internal sealed class GatedJob(bool failFirst = false) : ICliJob
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _current;
    private int _max;
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);
    public int Current => Volatile.Read(ref _current);
    public int MaxConcurrent => Volatile.Read(ref _max);

    public void Release() => _gate.TrySetResult();

    public async Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        if (failFirst && call == 1) throw new Exception("first call fails");

        var current = Interlocked.Increment(ref _current);
        int max;
        while (current > (max = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, current, max) != max)
        {
        }

        try
        {
            await _gate.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }
}

internal static class Until
{
    /// <summary>Waits for a condition the background runs bring about (fails after 10 s, which no passing run takes).</summary>
    public static async Task TrueAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting until {because}");
            await Task.Delay(5);
        }
    }
}

#endregion

public class JobConcurrencyTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 10, 30, 0, TimeSpan.Zero);

    private readonly InMemoryJobStorageProvider _storage = new();
    private readonly JobTestClock _time = new(Start);
    private readonly CliEventBus _bus = new();
    private readonly ConcurrentQueue<string> _events = new();
    private readonly CliJobScheduler _scheduler;

    public JobConcurrencyTests()
    {
        _bus.Subscribe("*", (e, _) =>
        {
            _events.Enqueue(e.Type);
            return Task.CompletedTask;
        });
        _scheduler = new CliJobScheduler(_storage, _bus, NullLogger<CliJobScheduler>.Instance, null, _time);
    }

    public void Dispose()
    {
        _scheduler.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _scheduler.Dispose();
        GC.SuppressFinalize(this);
    }

    private JobRegistration Register(ICliJob job, CliJobOptions options)
    {
        options.Name ??= "job";
        _scheduler.Register(job, options);
        return _scheduler.Jobs[options.Name];
    }

    private static Task IdleAsync(JobRegistration reg) =>
        Until.TrueAsync(() => reg.RunningExecutionIds.Count == 0 && !reg.Queued, "the job is idle");

    // --- Slots ---

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ConcurrentDueRuns_NeverExceedMaxConcurrentRuns(int max)
    {
        var job = new GatedJob();
        var reg = Register(job, new CliJobOptions { MaxConcurrentRuns = max, Interval = TimeSpan.FromHours(1) });
        await _scheduler.StartAsync(CancellationToken.None);

        // Timer ticks and triggers at the same moment
        var refused = 0;
        using var go = new ManualResetEventSlim();
        var callers = Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
        {
            go.Wait();
            if (i % 2 == 0)
            {
                _scheduler.OnTimerFired(reg);
                return;
            }
            try
            {
                await _scheduler.TriggerAsync(reg.Id);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref refused);
            }
        })).ToArray();
        go.Set();
        await Task.WhenAll(callers);

        await Until.TrueAsync(() => job.Current == max, $"{max} runs are going");
        Assert.Equal(max, reg.RunningExecutionIds.Count);
        // Every trigger that found no free slot was refused (Skip)
        Assert.True(refused >= 16 - max, $"{refused} triggers refused");

        job.Release();
        await IdleAsync(reg);
        Assert.Equal(max, job.Calls);
        Assert.Equal(max, job.MaxConcurrent);
    }

    [Fact]
    public async Task ARetryDelay_KeepsTheSlot_Skip()
    {
        var job = new GatedJob(failFirst: true);
        var reg = Register(job, new CliJobOptions
        {
            MaxRetries = 1,
            RetryDelay = TimeSpan.FromMinutes(1),
            RetryStrategy = JobRetryStrategy.Fixed,
        });
        await _scheduler.StartAsync(CancellationToken.None);

        await _scheduler.TriggerAsync(reg.Id);
        await Until.TrueAsync(() => _time.ArmedTimers == 1, "the run waits for its retry");
        var failed = Assert.Single((await _storage.GetExecutionsAsync(reg.Id)).Items);
        Assert.Equal(JobExecutionStatus.Failed, failed.Status);

        // The run still holds its slot: a tick is dropped, a trigger refused
        Assert.Equal([failed.Id], reg.RunningExecutionIds);
        Assert.Equal(failed.Id, reg.CurrentExecutionId);
        _scheduler.OnTimerFired(reg);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _scheduler.TriggerAsync(reg.Id));
        Assert.Equal(1, job.Calls);

        job.Release();
        _time.Advance(TimeSpan.FromMinutes(1));
        await IdleAsync(reg);
        Assert.Equal(2, job.Calls);
        Assert.Equal(1, job.MaxConcurrent);
    }

    [Fact]
    public async Task ARetryDelay_KeepsTheSlot_Queue()
    {
        var job = new GatedJob(failFirst: true);
        var reg = Register(job, new CliJobOptions
        {
            MaxRetries = 1,
            RetryDelay = TimeSpan.FromMinutes(1),
            RetryStrategy = JobRetryStrategy.Fixed,
            OverlapPolicy = JobOverlapPolicy.Queue,
        });
        await _scheduler.StartAsync(CancellationToken.None);

        await _scheduler.TriggerAsync(reg.Id);
        await Until.TrueAsync(() => _time.ArmedTimers == 1, "the run waits for its retry");

        _scheduler.OnTimerFired(reg);
        Assert.True(reg.Queued);
        Assert.Equal(1, job.Calls);

        job.Release();
        _time.Advance(TimeSpan.FromMinutes(1));
        await IdleAsync(reg);
        // The first call failed, its retry and then the queued run went
        Assert.Equal(3, job.Calls);
        Assert.Equal(1, job.MaxConcurrent);
    }

    [Fact]
    public async Task Queue_CoalescesDueRuns()
    {
        var job = new GatedJob();
        var reg = Register(job, new CliJobOptions { OverlapPolicy = JobOverlapPolicy.Queue });
        await _scheduler.StartAsync(CancellationToken.None);

        await _scheduler.TriggerAsync(reg.Id);
        await Until.TrueAsync(() => job.Current == 1, "the first run goes");

        // Three due runs while full: a trigger is accepted (queued), ticks queue too
        await _scheduler.TriggerAsync(reg.Id);
        _scheduler.OnTimerFired(reg);
        _scheduler.OnTimerFired(reg);
        Assert.True(reg.Queued);
        Assert.Single(reg.RunningExecutionIds);

        job.Release();
        await IdleAsync(reg);
        Assert.Equal(2, job.Calls);
        Assert.Equal(1, job.MaxConcurrent);
        Assert.Equal(2, (await _storage.GetExecutionsAsync(reg.Id)).Total);
    }

    [Fact]
    public async Task Cancel_CancelsTheOldestRun()
    {
        var job = new GatedJob();
        var reg = Register(job, new CliJobOptions { MaxConcurrentRuns = 2, OverlapPolicy = JobOverlapPolicy.Cancel });
        await _scheduler.StartAsync(CancellationToken.None);

        await _scheduler.TriggerAsync(reg.Id);
        await Until.TrueAsync(() => job.Current == 1, "the first run goes");
        await _scheduler.TriggerAsync(reg.Id);
        await Until.TrueAsync(() => job.Current == 2, "the second run goes");
        var oldest = reg.RunningExecutionIds[0];
        var newer = reg.RunningExecutionIds[1];

        await _scheduler.TriggerAsync(reg.Id);
        await Until.TrueAsync(() => !reg.RunningExecutionIds.Contains(oldest) && job.Current == 2, "the oldest run made room");

        Assert.Equal(JobExecutionStatus.Cancelled, (await _storage.GetExecutionAsync(oldest))!.Status);
        Assert.Equal(JobExecutionStatus.Running, (await _storage.GetExecutionAsync(newer))!.Status);
        Assert.Equal(newer, reg.RunningExecutionIds[0]);
        Assert.Equal(2, reg.RunningExecutionIds.Count);
        Assert.Equal(2, job.MaxConcurrent);

        job.Release();
        await IdleAsync(reg);
        Assert.Equal(3, job.Calls);
    }

    [Fact]
    public async Task CancelAndStop_CancelEveryRun_AndDropTheQueuedOne()
    {
        var job = new GatedJob();
        var reg = Register(job, new CliJobOptions { MaxConcurrentRuns = 2, OverlapPolicy = JobOverlapPolicy.Queue });
        await _scheduler.StartAsync(CancellationToken.None);

        await _scheduler.TriggerAsync(reg.Id);
        await _scheduler.TriggerAsync(reg.Id);
        await _scheduler.TriggerAsync(reg.Id);
        Assert.True(reg.Queued);
        await Until.TrueAsync(() => job.Current == 2, "two runs go");

        await _scheduler.CancelCurrentAsync(reg.Id);
        await IdleAsync(reg);
        Assert.Equal(2, job.Calls);
        var (items, _) = await _storage.GetExecutionsAsync(reg.Id);
        Assert.All(items, e => Assert.Equal(JobExecutionStatus.Cancelled, e.Status));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _scheduler.CancelCurrentAsync(reg.Id));

        await _scheduler.TriggerAsync(reg.Id);
        await _scheduler.TriggerAsync(reg.Id);
        await _scheduler.TriggerAsync(reg.Id);
        await _scheduler.StopJobAsync(reg.Id);
        await IdleAsync(reg);
        Assert.Equal(4, job.Calls);
        Assert.Equal(JobStatus.Stopped, reg.Status);
    }

    [Fact]
    public async Task LoweringMaxConcurrentRuns_CancelsNothing_RaisingItStartsTheQueuedRun()
    {
        var job = new GatedJob();
        var reg = Register(job, new CliJobOptions { MaxConcurrentRuns = 2, OverlapPolicy = JobOverlapPolicy.Queue });
        await _scheduler.StartAsync(CancellationToken.None);
        await _scheduler.TriggerAsync(reg.Id);
        await _scheduler.TriggerAsync(reg.Id);
        await Until.TrueAsync(() => job.Current == 2, "two runs go");

        await _scheduler.UpdateOptionsAsync(reg.Id, o => o.MaxConcurrentRuns = 1);
        Assert.Equal(2, reg.RunningExecutionIds.Count);
        await _scheduler.TriggerAsync(reg.Id);
        Assert.True(reg.Queued);

        await _scheduler.UpdateOptionsAsync(reg.Id, o => o.MaxConcurrentRuns = 3);
        await Until.TrueAsync(() => job.Current == 3, "the queued run started");
        Assert.False(reg.Queued);

        job.Release();
        await IdleAsync(reg);
    }

    [Fact]
    public async Task MaxConcurrentRuns_BelowOne_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _scheduler.Register(new GatedJob(), new CliJobOptions { Name = "bad", MaxConcurrentRuns = 0 }));

        var reg = Register(new GatedJob(), new CliJobOptions { MaxConcurrentRuns = 2 });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _scheduler.UpdateOptionsAsync(reg.Id, o => o.MaxConcurrentRuns = 0));
        Assert.Equal(2, reg.Options.MaxConcurrentRuns);
    }

    // --- Run on start ---

    private async Task<JobRegistration> StartWithLastRunAsync(GatedJob job, CliJobOptions options, DateTimeOffset? lastRunAt)
    {
        options.RunOnStart = true;
        var reg = Register(job, options);
        if (lastRunAt != null)
            await _storage.SaveJobStateAsync(reg.Id, new JobState { LastRunAt = lastRunAt.Value.UtcDateTime });
        await _scheduler.StartAsync(CancellationToken.None);
        // An interval job's run on start is its timer, due now
        _time.Advance(TimeSpan.Zero);
        return reg;
    }

    [Fact]
    public async Task RunOnStart_IntervalJob_NotRunWhenItRanRecently()
    {
        var job = new GatedJob();
        var reg = await StartWithLastRunAsync(job, new CliJobOptions { Interval = TimeSpan.FromHours(1) }, Start.AddMinutes(-1));

        Assert.Empty(reg.RunningExecutionIds);
        Assert.Equal(0, job.Calls);
        // The timer keeps the rhythm from the last run
        Assert.Equal(Start.AddMinutes(59).UtcDateTime, reg.NextRunAt);
    }

    [Fact]
    public async Task RunOnStart_IntervalJob_RunsWhenDue()
    {
        var job = new GatedJob();
        var reg = await StartWithLastRunAsync(job, new CliJobOptions { Interval = TimeSpan.FromHours(1) }, Start.AddHours(-2));

        Assert.Single(reg.RunningExecutionIds);
        job.Release();
        await IdleAsync(reg);
        Assert.Equal(1, job.Calls);
    }

    [Fact]
    public async Task RunOnStart_IntervalJob_RunsWhenItNeverRan()
    {
        var job = new GatedJob();
        var reg = await StartWithLastRunAsync(job, new CliJobOptions { Interval = TimeSpan.FromHours(1) }, null);

        Assert.Single(reg.RunningExecutionIds);
        job.Release();
        await IdleAsync(reg);
    }

    [Fact]
    public async Task RunOnStart_CronJob_NotRunWithoutAMissedOccurrence()
    {
        var job = new GatedJob();
        // Hourly; last run at 10:00, now 10:30: the 11:00 occurrence is still to come
        var reg = await StartWithLastRunAsync(job, new CliJobOptions { Schedule = "0 * * * *" }, Start.AddMinutes(-30));

        Assert.Empty(reg.RunningExecutionIds);
        Assert.Equal(0, job.Calls);
    }

    [Fact]
    public async Task RunOnStart_CronJob_RunsAfterAMissedOccurrence()
    {
        var job = new GatedJob();
        // Last run at 9:00, now 10:30: 10:00 was missed
        var reg = await StartWithLastRunAsync(job, new CliJobOptions { Schedule = "0 * * * *" }, Start.AddMinutes(-90));

        Assert.Single(reg.RunningExecutionIds);
        job.Release();
        await IdleAsync(reg);
    }

    [Fact]
    public async Task RunOnStart_CronJob_RunsWhenItNeverRan()
    {
        var job = new GatedJob();
        var reg = await StartWithLastRunAsync(job, new CliJobOptions { Schedule = "0 * * * *" }, null);

        Assert.Single(reg.RunningExecutionIds);
        job.Release();
        await IdleAsync(reg);
    }

    [Fact]
    public async Task RunOnStart_ManualJob_RunsOnEveryStart()
    {
        var job = new GatedJob();
        var reg = await StartWithLastRunAsync(job, new CliJobOptions(), Start.AddSeconds(-5));

        Assert.Single(reg.RunningExecutionIds);
        job.Release();
        await IdleAsync(reg);
    }

    // --- Cron ---

    [Fact]
    public async Task Cron_ATickThatFiresEarly_DoesNotRunTheOccurrenceTwice()
    {
        var job = new GatedJob();
        job.Release();
        var reg = Register(job, new CliJobOptions { Schedule = "0 * * * *" });
        await _scheduler.StartAsync(CancellationToken.None);
        Assert.Equal(Start.AddMinutes(30).UtcDateTime, reg.NextRunAt);

        // The 11:00 timer fires 5 ms early
        _time.SetUtcNow(Start.AddMinutes(30).AddMilliseconds(-5));
        _time.FireAll();
        await IdleAsync(reg);
        Assert.Equal(1, job.Calls);
        Assert.Equal(Start.AddMinutes(90).UtcDateTime, reg.NextRunAt);

        // 11:00 itself passes: nothing else is due before 12:00
        _time.Advance(TimeSpan.FromMilliseconds(10));
        await IdleAsync(reg);
        Assert.Equal(1, job.Calls);
        Assert.Equal(1, _time.ArmedTimers);
    }

    [Fact]
    public async Task Cron_AfterABusySpell_TheNextOccurrenceIsAfterNow()
    {
        var job = new GatedJob();
        job.Release();
        var reg = Register(job, new CliJobOptions { Schedule = "0 * * * *" });
        await _scheduler.StartAsync(CancellationToken.None);

        // The 11:00 tick comes at 13:20: it runs once and the next is 14:00, not 12:00
        _time.SetUtcNow(Start.AddHours(2).AddMinutes(50));
        _time.FireAll();
        await IdleAsync(reg);
        Assert.Equal(1, job.Calls);
        Assert.Equal(Start.AddHours(3).AddMinutes(30).UtcDateTime, reg.NextRunAt);
    }

    // --- Interrupted runs ---

    private static JobExecution Running(string jobId, DateTime startedAt) => new()
    {
        JobId = jobId,
        JobName = jobId,
        Status = JobExecutionStatus.Running,
        StartedAt = startedAt,
    };

    [Fact]
    public async Task InterruptedRuns_AreFinishedAsCancelledOnStart()
    {
        var reg = Register(new GatedJob(), new CliJobOptions { Name = "job", Interval = TimeSpan.FromHours(1) });
        var startedAt = Start.AddMinutes(-10).UtcDateTime;
        var first = Running("job", startedAt);
        var second = Running("job", startedAt.AddMinutes(1));
        var other = Running("not-registered", startedAt);
        var done = Running("job", startedAt);
        done.Status = JobExecutionStatus.Completed;
        foreach (var e in new[] { first, second, other, done })
            await _storage.SaveExecutionAsync(e);

        await _scheduler.StartAsync(CancellationToken.None);

        foreach (var e in new[] { first, second })
        {
            var saved = (await _storage.GetExecutionAsync(e.Id))!;
            Assert.Equal(JobExecutionStatus.Cancelled, saved.Status);
            Assert.Equal(CliJobScheduler.InterruptedError, saved.Error);
            Assert.Equal(saved.StartedAt, saved.CompletedAt);
            Assert.Equal(0, saved.Duration);
        }
        Assert.Equal(JobExecutionStatus.Running, (await _storage.GetExecutionAsync(other.Id))!.Status);
        Assert.Equal(JobExecutionStatus.Completed, (await _storage.GetExecutionAsync(done.Id))!.Status);
        Assert.Empty(reg.RunningExecutionIds);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task InterruptedRuns_AreFinishedInTheEfCoreStore()
    {
        var options = new DbContextOptionsBuilder<JobStorageDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var storage = new EfCoreJobStorageProvider(new DbFactory(options));
        var startedAt = Start.AddMinutes(-10).UtcDateTime;
        var runs = Enumerable.Range(0, 120).Select(i => Running("job", startedAt.AddSeconds(i))).ToList();
        foreach (var e in runs)
            await storage.SaveExecutionAsync(e);

        using var scheduler = new CliJobScheduler(storage, _bus, NullLogger<CliJobScheduler>.Instance, null, _time);
        scheduler.Register(new GatedJob(), new CliJobOptions { Name = "job" });
        await scheduler.StartAsync(CancellationToken.None);

        var (items, total) = await storage.GetExecutionsAsync("job", 200, 0, "Cancelled");
        Assert.Equal(120, total);
        Assert.All(items, e => Assert.Equal(CliJobScheduler.InterruptedError, e.Error));
        Assert.Equal(0, (await storage.GetExecutionsAsync("job", 1, 0, "Running")).Total);
        await scheduler.StopAsync(CancellationToken.None);
    }

    private sealed class DbFactory(DbContextOptions<JobStorageDbContext> options) : IDbContextFactory<JobStorageDbContext>
    {
        public JobStorageDbContext CreateDbContext() => new(options);
    }

    // --- API ---

    private static JsonElement Json(object dto) =>
        JsonSerializer.SerializeToElement(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private async Task<JsonElement> GetJobAsync(CliJobsController controller, string id) =>
        Json(Assert.IsType<OkObjectResult>(await controller.GetJob(id)).Value!);

    [Fact]
    public async Task Api_JobInfo_HasMaxConcurrentRunsRunningExecutionIdsAndQueued()
    {
        var job = new GatedJob();
        var reg = Register(job, new CliJobOptions { OverlapPolicy = JobOverlapPolicy.Queue });
        var controller = new CliJobsController(new SchedulerJobManager(_scheduler, _storage));
        await _scheduler.StartAsync(CancellationToken.None);

        var idle = await GetJobAsync(controller, reg.Id);
        Assert.Equal(1, idle.GetProperty("maxConcurrentRuns").GetInt32());
        Assert.Equal(0, idle.GetProperty("runningExecutionIds").GetArrayLength());
        Assert.False(idle.GetProperty("queued").GetBoolean());
        Assert.Equal(JsonValueKind.Null, idle.GetProperty("currentExecutionId").ValueKind);

        Assert.IsType<OkObjectResult>(await controller.TriggerJob(reg.Id, CancellationToken.None));
        // Queued, not refused: 200 like a started run
        Assert.IsType<OkObjectResult>(await controller.TriggerJob(reg.Id, CancellationToken.None));

        var busy = await GetJobAsync(controller, reg.Id);
        var running = busy.GetProperty("runningExecutionIds");
        Assert.Equal(1, running.GetArrayLength());
        Assert.Equal(running[0].GetString(), busy.GetProperty("currentExecutionId").GetString());
        Assert.True(busy.GetProperty("queued").GetBoolean());

        job.Release();
        await IdleAsync(reg);
    }

    [Fact]
    public async Task Api_Update_SetsAndValidatesMaxConcurrentRuns()
    {
        var reg = Register(new GatedJob(), new CliJobOptions());
        var controller = new CliJobsController(new SchedulerJobManager(_scheduler, _storage), _bus);

        Assert.IsType<OkObjectResult>(await controller.UpdateJob(reg.Id, new UpdateJobRequest { MaxConcurrentRuns = 3 }, CancellationToken.None));
        Assert.Equal(3, (await GetJobAsync(controller, reg.Id)).GetProperty("maxConcurrentRuns").GetInt32());
        Assert.Contains("job:updated", _events);

        foreach (var invalid in new[] { 0, -1 })
        {
            var bad = Assert.IsType<BadRequestObjectResult>(
                await controller.UpdateJob(reg.Id, new UpdateJobRequest { MaxConcurrentRuns = invalid }, CancellationToken.None));
            Assert.Equal("INVALID_VALUE", Json(bad.Value!).GetProperty("code").GetString());
        }
        Assert.Equal(3, reg.Options.MaxConcurrentRuns);
    }

    [Fact]
    public async Task Api_Trigger_RefusedWhenFullAndSkip()
    {
        var job = new GatedJob();
        var reg = Register(job, new CliJobOptions());
        var controller = new CliJobsController(new SchedulerJobManager(_scheduler, _storage));
        await _scheduler.StartAsync(CancellationToken.None);

        Assert.IsType<OkObjectResult>(await controller.TriggerJob(reg.Id, CancellationToken.None));
        var conflict = Assert.IsType<ConflictObjectResult>(await controller.TriggerJob(reg.Id, CancellationToken.None));
        Assert.Equal("JOB_ALREADY_RUNNING", Json(conflict.Value!).GetProperty("code").GetString());

        job.Release();
        await IdleAsync(reg);
    }

    [Fact]
    public void Api_AManagerWithoutTheNewFields_ReportsDefaults()
    {
        var info = new CliJobInfo { Id = "x", Name = "x", CurrentExecutionId = "e1" };
        var dto = Json(CliJobsController.MapJobDto(info));
        Assert.Equal(1, dto.GetProperty("maxConcurrentRuns").GetInt32());
        Assert.Equal("e1", dto.GetProperty("runningExecutionIds")[0].GetString());
        Assert.False(dto.GetProperty("queued").GetBoolean());
    }
}
