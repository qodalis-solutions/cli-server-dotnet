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

    /// <summary>Only executions started at or after this time (UTC; see <see cref="AsUtc"/>); null for any.</summary>
    public DateTime? Since { get; set; }

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
            var (items, count) = await listJob(jobId, Since == null ? Offset + Limit : int.MaxValue, Status);
            var matching = Since is not { } since ? items : items.Where(e => e.StartedAt >= AsUtc(since)).ToList();
            all.AddRange(matching);
            total += Since == null ? count : matching.Count;
        }

        return ([.. all.OrderByDescending(e => e.StartedAt).Skip(Offset).Take(Limit)], total);
    }

    /// <summary>
    /// A time as UTC: one without a kind (parsed without an offset, or read from a database) is taken as UTC, a
    /// local one is converted. Stores compare execution times, which are UTC, against it.
    /// </summary>
    public static DateTime AsUtc(DateTime time) => time.Kind switch
    {
        DateTimeKind.Utc => time,
        DateTimeKind.Local => time.ToUniversalTime(),
        _ => DateTime.SpecifyKind(time, DateTimeKind.Utc),
    };
}
