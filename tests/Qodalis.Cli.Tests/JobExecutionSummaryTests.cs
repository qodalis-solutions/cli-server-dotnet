using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Plugin.Jobs;

namespace Qodalis.Cli.Tests;

/// <summary>The runs every store's summary test saves, and what the summary of the last 24 hours must be.</summary>
internal static class JobSummaryCases
{
    public static async Task SeedAsync(ICliJobStorageProvider store, DateTime now)
    {
        foreach (var (job, status, hoursAgo) in new[]
        {
            ("a", JobExecutionStatus.Completed, 1), ("a", JobExecutionStatus.Completed, 3), ("a", JobExecutionStatus.Failed, 2),
            ("b", JobExecutionStatus.Failed, 5), ("a", JobExecutionStatus.Completed, 30), ("c", JobExecutionStatus.Completed, 48),
        })
        {
            await store.SaveExecutionAsync(new JobExecution
            {
                JobId = job, JobName = job, Status = status, StartedAt = now.AddHours(-hoursAgo),
                Logs = [new JobLogEntry { Level = "info", Message = "ran" }],
            });
        }
    }

    public static void AssertSummary(IReadOnlyList<JobExecutionSummary> summary, DateTime now)
    {
        Assert.Equal(["a:Completed:2", "a:Failed:1", "b:Failed:1"], summary.Select(s => $"{s.JobId}:{s.Status}:{s.Count}"));
        Assert.Equal(now.AddHours(-1), summary[0].LastStartedAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(DateTimeKind.Utc, summary[0].LastStartedAt.Kind);
        Assert.Equal(now.AddHours(-5), summary[2].LastStartedAt, TimeSpan.FromMilliseconds(1));
    }
}

public class JobExecutionSummaryTests
{
    [Fact]
    public async Task InMemoryStore_SummarizesInMemory()
    {
        var store = new InMemoryJobStorageProvider();
        var now = DateTime.UtcNow;
        await JobSummaryCases.SeedAsync(store, now);

        JobSummaryCases.AssertSummary(await store.SummarizeExecutionsAsync(now.AddHours(-24)), now);
        Assert.Equal(["b:Failed:1"],
            (await store.SummarizeExecutionsAsync(now.AddHours(-24), ["b"])).Select(s => $"{s.JobId}:{s.Status}:{s.Count}"));
    }

    [Fact]
    public async Task AStoreWithoutASummary_GetsTheDefault_FromItsQuery()
    {
        var now = DateTime.UtcNow;
        ICliJobStorageProvider store = new ListOnlyStore(new InMemoryJobStorageProvider());
        await JobSummaryCases.SeedAsync(store, now);

        // The default query merges the per-job listings, so it needs the job ids
        JobSummaryCases.AssertSummary(await store.SummarizeExecutionsAsync(now.AddHours(-24), ["a", "b", "c"]), now);
        await Assert.ThrowsAsync<NotSupportedException>(() => store.SummarizeExecutionsAsync(now.AddHours(-24)));
    }

    [Fact]
    public async Task InMemoryStore_KeepsTheLogsInLists()
    {
        var store = new InMemoryJobStorageProvider();
        await store.SaveExecutionAsync(new JobExecution
        {
            JobId = "a", JobName = "a", Logs = [new JobLogEntry { Level = "info", Message = "hi" }],
        });

        var (items, _) = await store.GetExecutionsAsync("a");
        Assert.Single(items[0].Logs);
        Assert.Null(items[0].LogCount);
        Assert.Equal(1, items[0].LogEntryCount);
    }

    /// <summary>A third-party store with only the required members (no query or summary of its own).</summary>
    private sealed class ListOnlyStore(InMemoryJobStorageProvider inner) : ICliJobStorageProvider
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
