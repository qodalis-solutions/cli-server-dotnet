namespace Qodalis.Cli.Abstractions.Jobs;

/// <summary>
/// The runs of one job with one status started in a range (<see cref="ICliJobStorageProvider.SummarizeExecutionsAsync"/>):
/// how many there are and when the newest one started, without loading the runs.
/// </summary>
public sealed class JobExecutionSummary
{
    /// <summary>The job identifier.</summary>
    public required string JobId { get; init; }

    /// <summary>The status of the runs counted.</summary>
    public required JobExecutionStatus Status { get; init; }

    /// <summary>The number of runs.</summary>
    public required int Count { get; init; }

    /// <summary>When the newest of these runs started (UTC).</summary>
    public required DateTime LastStartedAt { get; init; }

    /// <summary>
    /// Groups <paramref name="runs"/> by job and status, ordered by job id and status: the summary of stores that
    /// hold their runs in memory, and the interface's default.
    /// </summary>
    public static IReadOnlyList<JobExecutionSummary> Of(IEnumerable<JobExecution> runs) =>
        runs.GroupBy(e => (e.JobId, e.Status))
            .Select(g => new JobExecutionSummary
            {
                JobId = g.Key.JobId,
                Status = g.Key.Status,
                Count = g.Count(),
                LastStartedAt = g.Max(e => JobExecutionQuery.AsUtc(e.StartedAt)),
            })
            .OrderBy(s => s.JobId, StringComparer.Ordinal).ThenBy(s => s.Status)
            .ToList();
}
