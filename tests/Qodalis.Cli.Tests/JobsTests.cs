using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Plugin.Jobs;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests;

#region Helpers

public class DummyJob : ICliJob
{
    private readonly TimeSpan _delay;
    private readonly bool _fail;
    public int CallCount { get; private set; }

    public DummyJob(TimeSpan? delay = null, bool fail = false)
    {
        _delay = delay ?? TimeSpan.Zero;
        _fail = fail;
    }

    public async Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken)
    {
        CallCount++;
        context.Logger.Info("started");
        if (_delay > TimeSpan.Zero)
            await Task.Delay(_delay, cancellationToken);
        if (_fail)
            throw new Exception("boom");
        context.Logger.Info("done");
    }
}

public class SlowCancellableJob : ICliJob
{
    public async Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken)
    {
        context.Logger.Info("starting slow job");
        for (int i = 0; i < 50; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(20, cancellationToken);
        }
        context.Logger.Info("slow job completed");
    }
}

#endregion

#region IntervalParser

public class IntervalParserTests
{
    [Fact]
    public void Parse_Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), IntervalParser.Parse("30s"));
    }

    [Fact]
    public void Parse_Minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), IntervalParser.Parse("5m"));
    }

    [Fact]
    public void Parse_Hours()
    {
        Assert.Equal(TimeSpan.FromHours(1), IntervalParser.Parse("1h"));
    }

    [Fact]
    public void Parse_Days()
    {
        Assert.Equal(TimeSpan.FromDays(1), IntervalParser.Parse("1d"));
    }

    [Fact]
    public void Parse_Invalid_Throws()
    {
        Assert.Throws<ArgumentException>(() => IntervalParser.Parse("abc"));
    }

    [Fact]
    public void Parse_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => IntervalParser.Parse(""));
    }
}

#endregion

#region InMemoryJobStorageProvider

public class InMemoryJobStorageProviderTests
{
    private readonly InMemoryJobStorageProvider _storage = new();

    [Fact]
    public async Task SaveAndGetExecution()
    {
        var ex = new JobExecution { JobId = "j1", JobName = "test" };
        ex.Status = JobExecutionStatus.Completed;
        await _storage.SaveExecutionAsync(ex);

        var result = await _storage.GetExecutionAsync(ex.Id);
        Assert.NotNull(result);
        Assert.Equal(ex.Id, result!.Id);
    }

    [Fact]
    public async Task GetExecutions_Pagination()
    {
        for (int i = 0; i < 5; i++)
        {
            var ex = new JobExecution { JobId = "j1", JobName = "t" };
            ex.Status = JobExecutionStatus.Completed;
            await _storage.SaveExecutionAsync(ex);
        }

        var (items, total) = await _storage.GetExecutionsAsync("j1", limit: 2, offset: 1);
        Assert.Equal(5, total);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task GetExecutions_StatusFilter()
    {
        var ex1 = new JobExecution { JobId = "j1", JobName = "t" };
        ex1.Status = JobExecutionStatus.Completed;
        await _storage.SaveExecutionAsync(ex1);

        var ex2 = new JobExecution { JobId = "j1", JobName = "t" };
        ex2.Status = JobExecutionStatus.Failed;
        await _storage.SaveExecutionAsync(ex2);

        var (items, total) = await _storage.GetExecutionsAsync("j1", statusFilter: "failed");
        Assert.Equal(1, total);
        Assert.Equal(ex2.Id, items[0].Id);
    }

    [Fact]
    public async Task SaveAndGetJobState()
    {
        var state = new JobState { Status = JobStatus.Paused };
        await _storage.SaveJobStateAsync("j1", state);

        var result = await _storage.GetJobStateAsync("j1");
        Assert.NotNull(result);
        Assert.Equal(JobStatus.Paused, result!.Status);
    }

    [Fact]
    public async Task GetAllJobStates()
    {
        await _storage.SaveJobStateAsync("j1", new JobState { Status = JobStatus.Active });
        await _storage.SaveJobStateAsync("j2", new JobState { Status = JobStatus.Stopped });

        var states = await _storage.GetAllJobStatesAsync();
        Assert.Equal(2, states.Count);
    }

    [Fact]
    public async Task GetNonexistentExecution_ReturnsNull()
    {
        var result = await _storage.GetExecutionAsync("nope");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetNonexistentState_ReturnsNull()
    {
        var result = await _storage.GetJobStateAsync("nope");
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateExecution_NoDuplicate()
    {
        var ex = new JobExecution { JobId = "j1", JobName = "t" };
        ex.Status = JobExecutionStatus.Running;
        await _storage.SaveExecutionAsync(ex);

        ex.Status = JobExecutionStatus.Completed;
        await _storage.SaveExecutionAsync(ex);

        var result = await _storage.GetExecutionAsync(ex.Id);
        Assert.Equal(JobExecutionStatus.Completed, result!.Status);

        var (_, total) = await _storage.GetExecutionsAsync("j1");
        Assert.Equal(1, total);
    }
}

#endregion

#region Scheduler

public class CliJobSchedulerTests : IDisposable
{
    private readonly InMemoryJobStorageProvider _storage;
    private readonly CliJobScheduler _scheduler;

    public CliJobSchedulerTests()
    {
        _storage = new InMemoryJobStorageProvider();
        var mockEventSocket = new Mock<CliEventSocketManager>();
        var mockLogger = new Mock<ILogger<CliJobScheduler>>();
        _scheduler = new CliJobScheduler(_storage, mockEventSocket.Object, mockLogger.Object);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Register_ReturnsId()
    {
        var job = new DummyJob();
        _scheduler.Register(job, new CliJobOptions { Name = "test", Interval = TimeSpan.FromSeconds(10) });
        Assert.Single(_scheduler.Jobs);
    }

    [Fact]
    public void Register_DefaultsNameToClassName()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Interval = TimeSpan.FromSeconds(10) });
        var reg = _scheduler.Jobs.Values.First();
        Assert.Equal("DummyJob", reg.Options.Name);
    }

    [Fact]
    public void Register_Disabled_SetsStopped()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions
        {
            Name = "t",
            Interval = TimeSpan.FromSeconds(10),
            Enabled = false
        });
        var reg = _scheduler.Jobs.Values.First();
        Assert.Equal(JobStatus.Stopped, reg.Status);
    }

    [Fact]
    public async Task Trigger_ExecutesJob()
    {
        var job = new DummyJob();
        _scheduler.Register(job, new CliJobOptions { Name = "t", Interval = TimeSpan.FromSeconds(999) });
        var id = _scheduler.Jobs.Keys.First();

        await _scheduler.StartAsync(CancellationToken.None);
        await _scheduler.TriggerAsync(id);
        await Task.Delay(200);

        Assert.Equal(1, job.CallCount);
        var (items, total) = await _storage.GetExecutionsAsync(id);
        Assert.Equal(1, total);
        Assert.Equal(JobExecutionStatus.Completed, items[0].Status);

        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Trigger_NotFound_Throws()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _scheduler.TriggerAsync("nonexistent"));
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Trigger_OverlapSkip_Throws()
    {
        var job = new DummyJob(delay: TimeSpan.FromSeconds(1));
        _scheduler.Register(job, new CliJobOptions
        {
            Name = "t",
            Interval = TimeSpan.FromSeconds(999),
            OverlapPolicy = JobOverlapPolicy.Skip
        });
        var id = _scheduler.Jobs.Keys.First();

        await _scheduler.StartAsync(CancellationToken.None);
        await _scheduler.TriggerAsync(id);
        await Task.Delay(50);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _scheduler.TriggerAsync(id));
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PauseAndResume()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "t", Interval = TimeSpan.FromSeconds(10) });
        var id = _scheduler.Jobs.Keys.First();

        await _scheduler.StartAsync(CancellationToken.None);

        await _scheduler.PauseAsync(id);
        Assert.Equal(JobStatus.Paused, _scheduler.Jobs[id].Status);
        var state = await _storage.GetJobStateAsync(id);
        Assert.NotNull(state);
        Assert.Equal(JobStatus.Paused, state!.Status);

        await _scheduler.ResumeAsync(id);
        Assert.Equal(JobStatus.Active, _scheduler.Jobs[id].Status);

        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Pause_AlreadyPaused_Throws()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "t", Interval = TimeSpan.FromSeconds(10) });
        var id = _scheduler.Jobs.Keys.First();

        await _scheduler.StartAsync(CancellationToken.None);
        await _scheduler.PauseAsync(id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _scheduler.PauseAsync(id));
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Resume_NotPaused_Throws()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "t", Interval = TimeSpan.FromSeconds(10) });
        var id = _scheduler.Jobs.Keys.First();

        await _scheduler.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _scheduler.ResumeAsync(id));
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopJob()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "t", Interval = TimeSpan.FromSeconds(10) });
        var id = _scheduler.Jobs.Keys.First();

        await _scheduler.StartAsync(CancellationToken.None);
        await _scheduler.StopJobAsync(id);
        Assert.Equal(JobStatus.Stopped, _scheduler.Jobs[id].Status);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancelCurrent_NotRunning_Throws()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "t", Interval = TimeSpan.FromSeconds(10) });
        var id = _scheduler.Jobs.Keys.First();

        await _scheduler.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _scheduler.CancelCurrentAsync(id));
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancelCurrent_AbortsRunningJob()
    {
        _scheduler.Register(new SlowCancellableJob(), new CliJobOptions { Name = "t", Interval = TimeSpan.FromSeconds(999) });
        var id = _scheduler.Jobs.Keys.First();

        await _scheduler.StartAsync(CancellationToken.None);
        await _scheduler.TriggerAsync(id);
        await Task.Delay(50);
        await _scheduler.CancelCurrentAsync(id);
        await Task.Delay(200);

        var (items, _) = await _storage.GetExecutionsAsync(id);
        Assert.NotEmpty(items);
        Assert.Equal(JobExecutionStatus.Cancelled, items[0].Status);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FailedJob_Retries()
    {
        var job = new DummyJob(fail: true);
        _scheduler.Register(job, new CliJobOptions
        {
            Name = "t",
            Interval = TimeSpan.FromSeconds(999),
            MaxRetries = 2,
        });
        var id = _scheduler.Jobs.Keys.First();

        await _scheduler.StartAsync(CancellationToken.None);
        await _scheduler.TriggerAsync(id);
        await Task.Delay(500);

        Assert.Equal(3, job.CallCount); // 1 original + 2 retries
        var (items, total) = await _storage.GetExecutionsAsync(id);
        Assert.Equal(3, total);
        Assert.All(items, e => Assert.Equal(JobExecutionStatus.Failed, e.Status));
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Timeout_CancelsJob()
    {
        var job = new DummyJob(delay: TimeSpan.FromSeconds(5));
        _scheduler.Register(job, new CliJobOptions
        {
            Name = "t",
            Interval = TimeSpan.FromSeconds(999),
            Timeout = TimeSpan.FromSeconds(1),
        });
        var id = _scheduler.Jobs.Keys.First();

        await _scheduler.StartAsync(CancellationToken.None);
        await _scheduler.TriggerAsync(id);
        await Task.Delay(1500);

        var (items, _) = await _storage.GetExecutionsAsync(id);
        Assert.Single(items);
        Assert.Equal(JobExecutionStatus.TimedOut, items[0].Status);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UpdateOptions()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "t", Interval = TimeSpan.FromSeconds(10) });
        var id = _scheduler.Jobs.Keys.First();

        await _scheduler.StartAsync(CancellationToken.None);
        await _scheduler.UpdateOptionsAsync(id, opts =>
        {
            opts.Description = "updated";
            opts.MaxRetries = 3;
            opts.OverlapPolicy = JobOverlapPolicy.Queue;
        });

        var reg = _scheduler.Jobs[id];
        Assert.Equal("updated", reg.Options.Description);
        Assert.Equal(3, reg.Options.MaxRetries);
        Assert.Equal(JobOverlapPolicy.Queue, reg.Options.OverlapPolicy);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAndStop_Lifecycle()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "t", Interval = TimeSpan.FromSeconds(999) });
        var id = _scheduler.Jobs.Keys.First();

        await _scheduler.StartAsync(CancellationToken.None);
        Assert.Equal(JobStatus.Active, _scheduler.Jobs[id].Status);
        Assert.NotNull(_scheduler.Jobs[id].NextRunAt);

        await _scheduler.StopAsync(CancellationToken.None);
        var state = await _storage.GetJobStateAsync(id);
        Assert.NotNull(state);
    }

    [Fact]
    public async Task Start_RestoresPausedState()
    {
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "t", Interval = TimeSpan.FromSeconds(10) });
        var id = _scheduler.Jobs.Keys.First();
        await _storage.SaveJobStateAsync(id, new JobState { Status = JobStatus.Paused });

        await _scheduler.StartAsync(CancellationToken.None);
        Assert.Equal(JobStatus.Paused, _scheduler.Jobs[id].Status);
        await _scheduler.StopAsync(CancellationToken.None);
    }
}

#endregion

#region Controller

public class CliJobsControllerTests : IDisposable
{
    private readonly InMemoryJobStorageProvider _storage;
    private readonly CliJobScheduler _scheduler;
    private readonly CliJobsController _controller;
    private readonly Dictionary<string, string> _jobIds = new(); // name -> id

    public CliJobsControllerTests()
    {
        _storage = new InMemoryJobStorageProvider();
        var mockEventSocket = new Mock<CliEventSocketManager>();
        var mockLogger = new Mock<ILogger<CliJobScheduler>>();
        _scheduler = new CliJobScheduler(_storage, mockEventSocket.Object, mockLogger.Object);

        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "quick", Interval = TimeSpan.FromSeconds(999) });
        _scheduler.Register(new SlowCancellableJob(), new CliJobOptions { Name = "slow", Interval = TimeSpan.FromSeconds(999) });

        foreach (var (id, reg) in _scheduler.Jobs)
            _jobIds[reg.Options.Name!] = id;

        _controller = new CliJobsController(_scheduler, _storage);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- List ---

    [Fact]
    public void ListJobs_ReturnsAll()
    {
        var result = _controller.ListJobs() as OkObjectResult;
        Assert.NotNull(result);
    }

    // --- Get ---

    [Fact]
    public void GetJob_Found()
    {
        var result = _controller.GetJob(_jobIds["quick"]) as OkObjectResult;
        Assert.NotNull(result);
    }

    [Fact]
    public void GetJob_NotFound()
    {
        var result = _controller.GetJob("nonexistent");
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(notFound.Value);
        Assert.Contains("JOB_NOT_FOUND", json);
    }

    // --- Trigger ---

    [Fact]
    public async Task TriggerJob_Success()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        var result = await _controller.TriggerJob(_jobIds["quick"], CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        await Task.Delay(100);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TriggerJob_NotFound()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        var result = await _controller.TriggerJob("nonexistent", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    // --- Pause ---

    [Fact]
    public async Task PauseJob_Success()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        var result = await _controller.PauseJob(_jobIds["quick"], CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PauseJob_AlreadyPaused()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        await _controller.PauseJob(_jobIds["quick"], CancellationToken.None);
        var result = await _controller.PauseJob(_jobIds["quick"], CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(((ConflictObjectResult)result).Value);
        Assert.Contains("JOB_ALREADY_PAUSED", json);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    // --- Resume ---

    [Fact]
    public async Task ResumeJob_Success()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        await _controller.PauseJob(_jobIds["quick"], CancellationToken.None);
        var result = await _controller.ResumeJob(_jobIds["quick"], CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ResumeJob_NotPaused()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        var result = await _controller.ResumeJob(_jobIds["quick"], CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(((ConflictObjectResult)result).Value);
        Assert.Contains("JOB_NOT_PAUSED", json);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    // --- Stop ---

    [Fact]
    public async Task StopJob_Success()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        var result = await _controller.StopJob(_jobIds["quick"], CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopJob_NotFound()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        var result = await _controller.StopJob("nonexistent", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    // --- Cancel ---

    [Fact]
    public async Task CancelJob_NotRunning()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        var result = await _controller.CancelJob(_jobIds["quick"], CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(((ConflictObjectResult)result).Value);
        Assert.Contains("JOB_NOT_RUNNING", json);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    // --- Update ---

    [Fact]
    public async Task UpdateJob_Success()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        var request = new UpdateJobRequest { Description = "updated", MaxRetries = 5 };
        var result = await _controller.UpdateJob(_jobIds["quick"], request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("updated", _scheduler.Jobs[_jobIds["quick"]].Options.Description);
        Assert.Equal(5, _scheduler.Jobs[_jobIds["quick"]].Options.MaxRetries);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UpdateJob_BothScheduleAndInterval_BadRequest()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        var request = new UpdateJobRequest { Schedule = "* * * * *", Interval = "10s" };
        var result = await _controller.UpdateJob(_jobIds["quick"], request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(((BadRequestObjectResult)result).Value);
        Assert.Contains("SCHEDULE_CONFLICT", json);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UpdateJob_NotFound()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        var request = new UpdateJobRequest { Description = "x" };
        var result = await _controller.UpdateJob("nonexistent", request, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    // --- History ---

    [Fact]
    public async Task History_Empty()
    {
        var result = await _controller.GetHistory(_jobIds["quick"], ct: CancellationToken.None) as OkObjectResult;
        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result!.Value);
        Assert.Contains("\"total\":0", json);
    }

    [Fact]
    public async Task History_AfterTrigger()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        await _controller.TriggerJob(_jobIds["quick"], CancellationToken.None);
        await Task.Delay(200);

        var result = await _controller.GetHistory(_jobIds["quick"], ct: CancellationToken.None) as OkObjectResult;
        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result!.Value);
        Assert.DoesNotContain("\"total\":0", json);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task History_NotFound()
    {
        var result = await _controller.GetHistory("nonexistent", ct: CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // --- Execution detail ---

    [Fact]
    public async Task ExecutionDetail_Success()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        await _controller.TriggerJob(_jobIds["quick"], CancellationToken.None);
        await Task.Delay(200);

        var (items, _) = await _storage.GetExecutionsAsync(_jobIds["quick"]);
        Assert.NotEmpty(items);
        var execId = items[0].Id;

        var result = await _controller.GetExecution(_jobIds["quick"], execId, CancellationToken.None) as OkObjectResult;
        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result!.Value);
        Assert.Contains("logs", json);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecutionDetail_NotFound()
    {
        var result = await _controller.GetExecution(_jobIds["quick"], "nonexistent", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(((NotFoundObjectResult)result).Value);
        Assert.Contains("EXECUTION_NOT_FOUND", json);
    }
}

#endregion
