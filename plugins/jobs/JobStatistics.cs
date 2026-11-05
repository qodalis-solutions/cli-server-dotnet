using Qodalis.Cli.Abstractions.Insights;
using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// The statistics of <c>GET /api/v1/qcli/jobs/stats</c>: outcomes per time column, totals and per-job figures of every
/// run started in a range, read through <see cref="ICliJobManager.QueryExecutionsAsync"/> so any job store works.
/// </summary>
public static class JobStatistics
{
    /// <summary>Executions asked for per page while reading the range.</summary>
    public const int PageSize = 500;

    /// <summary>The newest runs listed per job.</summary>
    public const int RecentRuns = 12;

    /// <summary>The outcome a run's status counts as: <c>succeeded</c>, <c>failed</c>, <c>running</c> or <c>cancelled</c>.</summary>
    public static string Outcome(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "completed" or "succeeded" => "succeeded",
        "failed" or "error" or "timed_out" or "timedout" => "failed",
        "running" or "pending" => "running",
        _ => "cancelled",
    };

    /// <summary>The outcome of <paramref name="execution"/>.</summary>
    public static string Outcome(JobExecution execution) => Outcome(StatusName(execution));

    /// <summary>The status as the API writes it, e.g. <c>completed</c>.</summary>
    public static string StatusName(JobExecution execution) => execution.Status.ToString().ToLowerInvariant();

    /// <summary>
    /// Every run of <paramref name="jobIds"/> (null for every job) started in <c>[Since, Until)</c> of
    /// <paramref name="range"/>, newest first, paging through the manager's query until it is exhausted.
    /// </summary>
    public static async Task<List<JobExecution>> CollectAsync(
        ICliJobManager jobs, IReadOnlyCollection<string>? jobIds, TimeBuckets range, CancellationToken cancellationToken)
    {
        var runs = new List<JobExecution>();
        if (jobIds is { Count: 0 }) return runs;

        var seen = new HashSet<(string, string)>();
        var offset = 0;
        while (true)
        {
            var (items, total) = await jobs.QueryExecutionsAsync(new JobExecutionQuery
            {
                JobIds = jobIds,
                Since = range.Since,
                Limit = PageSize,
                Offset = offset,
            }, cancellationToken);
            if (items.Count == 0) break;

            foreach (var run in items)
            {
                if (range.Contains(run.StartedAt) && seen.Add((run.JobId, run.Id))) runs.Add(run);
            }

            offset += items.Count;
            // Newest first: once a page reaches runs older than the range, a store that ignored Since is done too.
            if (offset >= total || items.Min(e => TimeBuckets.AsUtc(e.StartedAt)) < range.Since) break;
        }

        return [.. runs.OrderByDescending(e => e.StartedAt)];
    }

    /// <summary>The response body for <paramref name="runs"/> (newest first) in <paramref name="range"/>.</summary>
    public static object Compute(IReadOnlyList<JobExecution> runs, TimeBuckets range)
    {
        var buckets = Enumerable.Range(0, range.Count).Select(_ => new OutcomeCounts()).ToArray();
        foreach (var run in runs)
        {
            var index = range.IndexOf(run.StartedAt);
            if (index >= 0) buckets[index].Add(Outcome(run));
        }

        return new
        {
            since = TimeBuckets.Format(range.Since),
            until = TimeBuckets.Format(range.Until),
            bucketMs = range.BucketMs,
            buckets = buckets.Select((b, i) => new
            {
                start = TimeBuckets.Format(range.StartOf(i)),
                succeeded = b.Succeeded,
                failed = b.Failed,
                cancelled = b.Cancelled,
                running = b.Running,
            }),
            totals = Summary(runs),
            jobs = runs.GroupBy(r => r.JobId)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var jobRuns = g.ToList();
                    var s = Summary(jobRuns);
                    return new
                    {
                        jobId = g.Key,
                        jobName = jobRuns[0].JobName,
                        s.total,
                        s.succeeded,
                        s.failed,
                        s.cancelled,
                        s.running,
                        s.successRate,
                        s.avgDuration,
                        s.p95Duration,
                        s.maxDuration,
                        s.lastFailureAt,
                        recent = jobRuns.Take(RecentRuns).Select(r => new
                        {
                            status = StatusName(r),
                            startedAt = TimeBuckets.Format(r.StartedAt),
                            duration = r.Duration,
                        }),
                    };
                }),
        };
    }

    private static Totals Summary(IReadOnlyList<JobExecution> runs)
    {
        var counts = new OutcomeCounts();
        foreach (var run in runs) counts.Add(Outcome(run));

        var durations = runs
            .Where(r => r.Duration is not null && Outcome(r) != "running")
            .Select(r => r.Duration!.Value)
            .Order()
            .ToList();
        var lastFailure = runs.Where(r => Outcome(r) == "failed").Select(r => (DateTime?)r.StartedAt).Max();
        var decided = counts.Succeeded + counts.Failed;

        return new Totals(
            runs.Count,
            counts.Succeeded,
            counts.Failed,
            counts.Cancelled,
            counts.Running,
            decided == 0 ? null : Math.Round((double)counts.Succeeded / decided, 3),
            durations.Count == 0 ? null : (long)Math.Round(durations.Average()),
            TimeBuckets.Percentile(durations, 0.95),
            durations.Count == 0 ? null : durations[^1],
            lastFailure is { } at ? TimeBuckets.Format(at) : null);
    }

    // Lower-case members: the JSON names of the totals.
#pragma warning disable IDE1006
    private sealed record Totals(
        int total, int succeeded, int failed, int cancelled, int running,
        double? successRate, long? avgDuration, long? p95Duration, long? maxDuration, string? lastFailureAt);
#pragma warning restore IDE1006

    private sealed class OutcomeCounts
    {
        public int Succeeded;
        public int Failed;
        public int Cancelled;
        public int Running;

        public void Add(string outcome)
        {
            switch (outcome)
            {
                case "succeeded": Succeeded++; break;
                case "failed": Failed++; break;
                case "running": Running++; break;
                default: Cancelled++; break;
            }
        }
    }
}
