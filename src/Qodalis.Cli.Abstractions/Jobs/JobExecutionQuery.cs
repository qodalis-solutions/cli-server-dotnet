namespace Qodalis.Cli.Abstractions.Jobs;

/// <summary>
/// Which executions to list across jobs (<c>GET /api/v1/qcli/jobs/executions</c>), newest first.
/// </summary>
public class JobExecutionQuery
{
    /// <summary>Only the executions of these jobs; null for every job.</summary>
    public IReadOnlyCollection<string>? JobIds { get; set; }

    /// <summary>Only executions with this status (e.g. <c>failed</c>), case-insensitive; null for any.</summary>
    public string? Status { get; set; }

    /// <summary>Maximum number of executions to return.</summary>
    public int Limit { get; set; } = 20;

    /// <summary>Number of executions to skip.</summary>
    public int Offset { get; set; }

    /// <summary>
    /// Lists the executions of several jobs from a per-job listing: reads the first <c>Offset + Limit</c> of each job
    /// and merges them newest first. The default for stores and managers without a query of their own.
    /// </summary>
    public async Task<(IReadOnlyList<JobExecution> Items, int Total)> MergeAsync(
        IEnumerable<string> jobIds,
        Func<string, int, string?, Task<(IReadOnlyList<JobExecution> Items, int Total)>> listJob)
    {
        var all = new List<JobExecution>();
        var total = 0;
        foreach (var jobId in jobIds.Distinct())
        {
            var (items, count) = await listJob(jobId, Offset + Limit, Status);
            all.AddRange(items);
            total += count;
        }

        return ([.. all.OrderByDescending(e => e.StartedAt).Skip(Offset).Take(Limit)], total);
    }
}
