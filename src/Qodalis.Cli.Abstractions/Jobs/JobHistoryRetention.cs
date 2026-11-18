namespace Qodalis.Cli.Abstractions.Jobs;

/// <summary>
/// How long a job's history is kept: a run is deleted when it breaks any limit that is set. Failed runs use
/// <see cref="FailedMaxAge"/> and <see cref="FailedMaxRunsPerJob"/> when those are set, else the normal limits.
/// Running executions and the newest run of each job are never deleted.
/// </summary>
public class JobHistoryRetention
{
    /// <summary>Deletes runs that started longer ago than this.</summary>
    public TimeSpan? MaxAge { get; set; }

    /// <summary>
    /// Keeps at most this many runs per job: the job's runs are ranked newest first, and a run ranked at or past
    /// the limit is deleted.
    /// </summary>
    public int? MaxRunsPerJob { get; set; }

    /// <summary>Like <see cref="MaxAge"/> for failed runs (status <c>failed</c>); null uses <see cref="MaxAge"/>.</summary>
    public TimeSpan? FailedMaxAge { get; set; }

    /// <summary>
    /// Like <see cref="MaxRunsPerJob"/> for failed runs (ranked among all the job's runs); null uses
    /// <see cref="MaxRunsPerJob"/>.
    /// </summary>
    public int? FailedMaxRunsPerJob { get; set; }

    /// <summary>Whether any limit is set.</summary>
    public bool HasLimits =>
        MaxAge != null || MaxRunsPerJob != null || FailedMaxAge != null || FailedMaxRunsPerJob != null;

    /// <summary>The age limit for a run with <paramref name="status"/>.</summary>
    public TimeSpan? MaxAgeFor(JobExecutionStatus status) =>
        status == JobExecutionStatus.Failed ? FailedMaxAge ?? MaxAge : MaxAge;

    /// <summary>The count limit for a run with <paramref name="status"/> (at least 1: the newest run stays).</summary>
    public int? MaxRunsFor(JobExecutionStatus status) =>
        (status == JobExecutionStatus.Failed ? FailedMaxRunsPerJob ?? MaxRunsPerJob : MaxRunsPerJob) is { } n
            ? Math.Max(1, n)
            : null;

    /// <summary>
    /// The runs of one job to delete under these limits at <paramref name="now"/>: <paramref name="runs"/> are all the
    /// job's runs, newest first. For stores that hold the runs in memory and the default
    /// <see cref="ICliJobStorageProvider.PruneAsync(string, JobHistoryRetention, DateTimeOffset, CancellationToken)"/>.
    /// </summary>
    public IEnumerable<T> SelectExpired<T>(IReadOnlyList<T> runs, Func<T, JobExecutionStatus> status,
        Func<T, DateTime> startedAt, DateTimeOffset now)
    {
        // The newest run (index 0) always stays
        for (var rank = 1; rank < runs.Count; rank++)
        {
            var run = runs[rank];
            var runStatus = status(run);
            if (runStatus == JobExecutionStatus.Running) continue;
            var tooOld = MaxAgeFor(runStatus) is { } age
                && JobExecutionQuery.AsUtc(startedAt(run)) < now.UtcDateTime - age;
            var tooMany = MaxRunsFor(runStatus) is { } max && rank >= max;
            if (tooOld || tooMany) yield return run;
        }
    }
}

/// <summary>
/// A job's own history retention (<see cref="CliJobOptions.History"/>): a field that is set replaces the global one
/// for this job, and <see cref="KeepAll"/> keeps its whole history.
/// </summary>
public class CliJobHistoryOptions : JobHistoryRetention
{
    /// <summary>Keeps every run of this job, whatever the global limits.</summary>
    public bool KeepAll { get; set; }

    /// <summary>Whether this job overrides the global retention (a limit set, or <see cref="KeepAll"/>).</summary>
    public bool IsOverridden => KeepAll || HasLimits;

    /// <summary>
    /// The limits that apply to this job: its own fields over <paramref name="global"/>; null when none apply and
    /// it does not keep everything.
    /// </summary>
    public CliJobHistoryRetentionInfo? Resolve(JobHistoryRetention? global)
    {
        if (KeepAll)
            return new CliJobHistoryRetentionInfo { Overridden = true, KeepAll = true };

        var effective = new CliJobHistoryRetentionInfo
        {
            MaxAge = MaxAge ?? global?.MaxAge,
            MaxRunsPerJob = MaxRunsPerJob ?? global?.MaxRunsPerJob,
            FailedMaxAge = FailedMaxAge ?? global?.FailedMaxAge,
            FailedMaxRunsPerJob = FailedMaxRunsPerJob ?? global?.FailedMaxRunsPerJob,
            Overridden = HasLimits,
        };
        return effective.HasLimits ? effective : null;
    }
}

/// <summary>The history retention in effect for a job (<see cref="CliJobInfo.HistoryRetention"/>).</summary>
public class CliJobHistoryRetentionInfo : JobHistoryRetention
{
    /// <summary>Whether the job sets limits of its own (or keeps everything) instead of only the global ones.</summary>
    public bool Overridden { get; set; }

    /// <summary>Whether the job keeps its whole history.</summary>
    public bool KeepAll { get; set; }
}
