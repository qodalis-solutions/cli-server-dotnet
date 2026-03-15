using Microsoft.EntityFrameworkCore;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Plugin.Jobs.EfCore;

namespace Qodalis.Cli.Tests;

public class EfCoreJobStorageProviderTests : IDisposable
{
    private readonly EfCoreJobStorageProvider _provider;
    private readonly JobStorageDbContext _db;

    public EfCoreJobStorageProviderTests()
    {
        var options = new DbContextOptionsBuilder<JobStorageDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var factory = new TestDbContextFactory(options);
        _db = new JobStorageDbContext(options);
        _provider = new EfCoreJobStorageProvider(factory);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    // --- SaveExecution & GetExecution ---

    [Fact]
    public async Task SaveExecution_AndGetExecution_RoundTrip()
    {
        var execution = new JobExecution
        {
            JobId = "job-1",
            JobName = "Test Job",
            Status = JobExecutionStatus.Running,
            StartedAt = DateTime.UtcNow,
            Logs =
            [
                new JobLogEntry { Level = "info", Message = "Starting" },
            ],
        };

        await _provider.SaveExecutionAsync(execution);

        var result = await _provider.GetExecutionAsync(execution.Id);
        Assert.NotNull(result);
        Assert.Equal(execution.Id, result.Id);
        Assert.Equal("job-1", result.JobId);
        Assert.Equal("Test Job", result.JobName);
        Assert.Equal(JobExecutionStatus.Running, result.Status);
        Assert.Single(result.Logs);
        Assert.Equal("info", result.Logs[0].Level);
        Assert.Equal("Starting", result.Logs[0].Message);
    }

    [Fact]
    public async Task SaveExecution_UpdatesExisting()
    {
        var execution = new JobExecution
        {
            JobId = "job-1",
            JobName = "Test Job",
            Status = JobExecutionStatus.Running,
            StartedAt = DateTime.UtcNow,
        };

        await _provider.SaveExecutionAsync(execution);

        execution.Status = JobExecutionStatus.Completed;
        execution.CompletedAt = DateTime.UtcNow;
        execution.Duration = 1234;

        await _provider.SaveExecutionAsync(execution);

        var result = await _provider.GetExecutionAsync(execution.Id);
        Assert.NotNull(result);
        Assert.Equal(JobExecutionStatus.Completed, result.Status);
        Assert.NotNull(result.CompletedAt);
        Assert.Equal(1234, result.Duration);
    }

    [Fact]
    public async Task SaveExecution_AppendsNewLogs()
    {
        var execution = new JobExecution
        {
            JobId = "job-1",
            JobName = "Test Job",
            Logs = [new JobLogEntry { Level = "info", Message = "Step 1" }],
        };

        await _provider.SaveExecutionAsync(execution);

        execution.Logs.Add(new JobLogEntry { Level = "info", Message = "Step 2" });
        await _provider.SaveExecutionAsync(execution);

        var result = await _provider.GetExecutionAsync(execution.Id);
        Assert.NotNull(result);
        Assert.Equal(2, result.Logs.Count);
    }

    [Fact]
    public async Task GetExecution_ReturnsNullWhenNotFound()
    {
        var result = await _provider.GetExecutionAsync("nonexistent");
        Assert.Null(result);
    }

    // --- GetExecutions (pagination & filtering) ---

    [Fact]
    public async Task GetExecutions_ReturnsByJobId()
    {
        await _provider.SaveExecutionAsync(new JobExecution { JobId = "job-1", JobName = "Job 1" });
        await _provider.SaveExecutionAsync(new JobExecution { JobId = "job-1", JobName = "Job 1" });
        await _provider.SaveExecutionAsync(new JobExecution { JobId = "job-2", JobName = "Job 2" });

        var (items, total) = await _provider.GetExecutionsAsync("job-1");
        Assert.Equal(2, total);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("job-1", i.JobId));
    }

    [Fact]
    public async Task GetExecutions_Pagination()
    {
        for (int i = 0; i < 5; i++)
        {
            await _provider.SaveExecutionAsync(new JobExecution
            {
                JobId = "job-1",
                JobName = "Job 1",
                StartedAt = DateTime.UtcNow.AddMinutes(i),
            });
        }

        var (items, total) = await _provider.GetExecutionsAsync("job-1", limit: 2, offset: 1);
        Assert.Equal(5, total);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task GetExecutions_StatusFilter()
    {
        await _provider.SaveExecutionAsync(new JobExecution
        {
            JobId = "job-1", JobName = "Job 1", Status = JobExecutionStatus.Completed,
        });
        await _provider.SaveExecutionAsync(new JobExecution
        {
            JobId = "job-1", JobName = "Job 1", Status = JobExecutionStatus.Failed,
        });
        await _provider.SaveExecutionAsync(new JobExecution
        {
            JobId = "job-1", JobName = "Job 1", Status = JobExecutionStatus.Failed,
        });

        var (items, total) = await _provider.GetExecutionsAsync("job-1", statusFilter: "failed");
        Assert.Equal(2, total);
        Assert.All(items, i => Assert.Equal(JobExecutionStatus.Failed, i.Status));
    }

    [Fact]
    public async Task GetExecutions_EmptyForUnknownJob()
    {
        var (items, total) = await _provider.GetExecutionsAsync("nonexistent");
        Assert.Empty(items);
        Assert.Equal(0, total);
    }

    // --- JobState ---

    [Fact]
    public async Task SaveJobState_AndGetJobState_RoundTrip()
    {
        var state = new JobState
        {
            Status = JobStatus.Active,
            LastRunAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await _provider.SaveJobStateAsync("job-1", state);

        var result = await _provider.GetJobStateAsync("job-1");
        Assert.NotNull(result);
        Assert.Equal(JobStatus.Active, result.Status);
        Assert.NotNull(result.LastRunAt);
    }

    [Fact]
    public async Task SaveJobState_UpdatesExisting()
    {
        var state = new JobState { Status = JobStatus.Active };
        await _provider.SaveJobStateAsync("job-1", state);

        state.Status = JobStatus.Paused;
        state.UpdatedAt = DateTime.UtcNow;
        await _provider.SaveJobStateAsync("job-1", state);

        var result = await _provider.GetJobStateAsync("job-1");
        Assert.NotNull(result);
        Assert.Equal(JobStatus.Paused, result.Status);
    }

    [Fact]
    public async Task GetJobState_ReturnsNullWhenNotFound()
    {
        var result = await _provider.GetJobStateAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllJobStates_ReturnsAll()
    {
        await _provider.SaveJobStateAsync("job-1", new JobState { Status = JobStatus.Active });
        await _provider.SaveJobStateAsync("job-2", new JobState { Status = JobStatus.Paused });
        await _provider.SaveJobStateAsync("job-3", new JobState { Status = JobStatus.Stopped });

        var states = await _provider.GetAllJobStatesAsync();
        Assert.Equal(3, states.Count);
        Assert.Equal(JobStatus.Active, states["job-1"].Status);
        Assert.Equal(JobStatus.Paused, states["job-2"].Status);
        Assert.Equal(JobStatus.Stopped, states["job-3"].Status);
    }

    [Fact]
    public async Task GetAllJobStates_EmptyWhenNone()
    {
        var states = await _provider.GetAllJobStatesAsync();
        Assert.Empty(states);
    }

    // --- Test helper ---

    private class TestDbContextFactory : IDbContextFactory<JobStorageDbContext>
    {
        private readonly DbContextOptions<JobStorageDbContext> _options;

        public TestDbContextFactory(DbContextOptions<JobStorageDbContext> options)
        {
            _options = options;
        }

        public JobStorageDbContext CreateDbContext()
        {
            return new JobStorageDbContext(_options);
        }
    }
}
