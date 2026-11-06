using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Qodalis.Cli.Services;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Plugin.Jobs;

namespace Qodalis.Cli.Tests;

/// <summary>Deleting runs through <c>DELETE /jobs/{id}/history[/{execId}]</c>.</summary>
public class JobRunDeletionTests : IDisposable
{
    private readonly InMemoryJobStorageProvider _storage = new();
    private readonly CliJobScheduler _scheduler;
    private readonly CliJobsController _controller;
    private readonly string _jobId;
    private readonly string _otherJobId;

    public JobRunDeletionTests()
    {
        _scheduler = new CliJobScheduler(_storage, new Mock<ICliEventSocketManager>().Object,
            new Mock<ILogger<CliJobScheduler>>().Object);
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "quick", Interval = TimeSpan.FromSeconds(999) });
        _scheduler.Register(new DummyJob(), new CliJobOptions { Name = "other", Interval = TimeSpan.FromSeconds(999) });
        _jobId = _scheduler.Jobs.Single(j => j.Value.Options.Name == "quick").Key;
        _otherJobId = _scheduler.Jobs.Single(j => j.Value.Options.Name == "other").Key;
        _controller = new CliJobsController(new SchedulerJobManager(_scheduler, _storage));
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<JobExecution> RunAsync(string jobId, JobExecutionStatus status, string? id = null)
    {
        var execution = new JobExecution
        {
            Id = id ?? Guid.NewGuid().ToString(),
            JobId = jobId,
            JobName = jobId,
            Status = status,
            StartedAt = DateTime.UtcNow,
        };
        execution.Logs.Add(new JobLogEntry { Timestamp = DateTime.UtcNow, Level = "info", Message = "hello" });
        await _storage.SaveExecutionAsync(execution);
        return execution;
    }

    private static string Json(IActionResult result) => JsonSerializer.Serialize(((ObjectResult)result).Value);

    [Fact]
    public async Task DeleteExecution_RemovesTheRun()
    {
        var run = await RunAsync(_jobId, JobExecutionStatus.Failed);

        var result = await _controller.DeleteExecution(_jobId, run.Id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Contains("Run deleted", Json(result));
        Assert.Null(await _storage.GetExecutionAsync(run.Id));
    }

    [Fact]
    public async Task DeleteExecution_UnknownJob_404()
    {
        var result = await _controller.DeleteExecution("nope", "x", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains("JOB_NOT_FOUND", Json(result));
    }

    [Fact]
    public async Task DeleteExecution_UnknownRunOrAnotherJobsRun_404()
    {
        var other = await RunAsync(_otherJobId, JobExecutionStatus.Failed);

        foreach (var execId in new[] { "missing", other.Id })
        {
            var result = await _controller.DeleteExecution(_jobId, execId, CancellationToken.None);
            Assert.IsType<NotFoundObjectResult>(result);
            Assert.Contains("EXECUTION_NOT_FOUND", Json(result));
        }
        Assert.NotNull(await _storage.GetExecutionAsync(other.Id));
    }

    [Fact]
    public async Task DeleteExecution_Running_409()
    {
        var run = await RunAsync(_jobId, JobExecutionStatus.Running);

        var result = await _controller.DeleteExecution(_jobId, run.Id, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("EXECUTION_RUNNING", Json(result));
        Assert.NotNull(await _storage.GetExecutionAsync(run.Id));
    }

    [Fact]
    public async Task DeleteHistory_WithStatus_DeletesOnlyThoseRuns()
    {
        for (var i = 0; i < 3; i++) await RunAsync(_jobId, JobExecutionStatus.Failed);
        var completed = await RunAsync(_jobId, JobExecutionStatus.Completed);
        var otherFailed = await RunAsync(_otherJobId, JobExecutionStatus.Failed);

        var result = await _controller.DeleteHistory(_jobId, "failed", CancellationToken.None);

        Assert.Contains("\"deleted\":3", Json(result));
        var (left, total) = await _storage.GetExecutionsAsync(_jobId);
        Assert.Equal(1, total);
        Assert.Equal(completed.Id, left.Single().Id);
        Assert.NotNull(await _storage.GetExecutionAsync(otherFailed.Id));
    }

    [Fact]
    public async Task DeleteHistory_WithoutStatus_KeepsRunningRuns()
    {
        // More runs than a page, so the default pages through the history
        for (var i = 0; i < 130; i++) await RunAsync(_jobId, i % 2 == 0 ? JobExecutionStatus.Completed : JobExecutionStatus.Failed);
        var running = await RunAsync(_jobId, JobExecutionStatus.Running);

        var result = await _controller.DeleteHistory(_jobId, null, CancellationToken.None);

        Assert.Contains("\"deleted\":130", Json(result));
        var (left, _) = await _storage.GetExecutionsAsync(_jobId);
        Assert.Equal(running.Id, left.Single().Id);
    }

    [Fact]
    public async Task DeleteHistory_UnknownJob_404()
    {
        var result = await _controller.DeleteHistory("nope", null, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains("JOB_NOT_FOUND", Json(result));
    }

    [Fact]
    public async Task StoreThatDoesNotDelete_501()
    {
        var storage = new KeepingStorage(_storage);
        var controller = new CliJobsController(new SchedulerJobManager(_scheduler, storage));
        var run = await RunAsync(_jobId, JobExecutionStatus.Failed);

        var result = await controller.DeleteExecution(_jobId, run.Id, CancellationToken.None);

        Assert.Equal(501, ((ObjectResult)result).StatusCode);
        Assert.Contains("NOT_SUPPORTED", Json(result));
    }

    [Fact]
    public async Task ManagerWithoutDelete_501()
    {
        var controller = new CliJobsController(new FakeJobManager());

        foreach (var result in new[]
        {
            await controller.DeleteExecution("fetch-rates:bnr", "r1", CancellationToken.None),
            await controller.DeleteHistory("fetch-rates:bnr", "failed", CancellationToken.None),
        })
        {
            Assert.Equal(501, ((ObjectResult)result).StatusCode);
            Assert.Contains("NOT_SUPPORTED", Json(result));
        }
    }

    /// <summary>A store that keeps every run: <see cref="ICliJobStorageProvider.DeleteExecutionAsync"/>'s default.</summary>
    private sealed class KeepingStorage(InMemoryJobStorageProvider inner) : ICliJobStorageProvider
    {
        public Task SaveExecutionAsync(JobExecution execution, CancellationToken cancellationToken = default) =>
            inner.SaveExecutionAsync(execution, cancellationToken);

        public Task<(List<JobExecution> Items, int Total)> GetExecutionsAsync(string jobId, int limit = 20, int offset = 0,
            string? statusFilter = null, CancellationToken cancellationToken = default) =>
            inner.GetExecutionsAsync(jobId, limit, offset, statusFilter, cancellationToken);

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
