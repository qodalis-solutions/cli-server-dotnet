namespace Qodalis.Cli.Abstractions.Jobs;

/// <summary>
/// How long a job's history is kept: a run is deleted when it breaks any limit that is set. Failed runs (status
/// <c>failed</c> only) use <see cref="FailedMaxAge"/> and <see cref="FailedMaxRunsPerJob"/> when those are set, else
/// the normal limits. Running executions and the newest run of each job are never deleted, but take their place in
/// the count. The same rules on every server (.NET, Node, Python).
/// </summary>
public class JobHistoryRetention
{
    /// <summary>Deletes runs that started longer ago than this (positive).</summary>
    public TimeSpan? MaxAge { get; set; }

    /// <summary>
    /// Keeps at most this many runs per job (at least 1): the job's runs are ranked newest first and a run ranked at or
    /// past the limit is deleted. With <see cref="FailedMaxRunsPerJob"/> set, only the runs that did not fail are
    /// ranked against it; otherwise every run of the job is.
    /// </summary>
    public int? MaxRunsPerJob { get; set; }

    /// <summary>Like <see cref="MaxAge"/> for failed runs; null uses <see cref="MaxAge"/>.</summary>
    public TimeSpan? FailedMaxAge { get; set; }

    /// <summary>
    /// Keeps at most this many failed runs per job (at least 1), ranked among the job's failed runs only; null ranks
    /// failed runs with the others against <see cref="MaxRunsPerJob"/>.
    /// </summary>
    public int? FailedMaxRunsPerJob { get; set; }

    /// <summary>Whether any limit is set.</summary>
    public bool HasLimits =>
        MaxAge != null || MaxRunsPerJob != null || FailedMaxAge != null || FailedMaxRunsPerJob != null;

    /// <summary>Whether failed runs are counted on their own (<see cref="FailedMaxRunsPerJob"/> is set).</summary>
    public bool CountsFailedSeparately => FailedMaxRunsPerJob != null;

    /// <summary>The age limit for a run with <paramref name="status"/>.</summary>
    public TimeSpan? MaxAgeFor(JobExecutionStatus status) =>
        status == JobExecutionStatus.Failed ? FailedMaxAge ?? MaxAge : MaxAge;

    /// <summary>The count limit for a run with <paramref name="status"/>.</summary>
    public int? MaxRunsFor(JobExecutionStatus status) =>
        status == JobExecutionStatus.Failed && CountsFailedSeparately ? FailedMaxRunsPerJob : MaxRunsPerJob;

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> when a count limit is below 1 or a duration is not positive;
    /// <paramref name="owner"/> names where the limits come from in the message.
    /// </summary>
    public void Validate(string owner)
    {
        void Positive(TimeSpan? value, string name)
        {
            if (value is { } v && v <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(name, v, $"{owner}: History.{name} must be positive");
        }
        void AtLeastOne(int? value, string name)
        {
            if (value is { } v && v < 1)
                throw new ArgumentOutOfRangeException(name, v, $"{owner}: History.{name} must be an integer of at least 1");
        }
        Positive(MaxAge, nameof(MaxAge));
        Positive(FailedMaxAge, nameof(FailedMaxAge));
        AtLeastOne(MaxRunsPerJob, nameof(MaxRunsPerJob));
        AtLeastOne(FailedMaxRunsPerJob, nameof(FailedMaxRunsPerJob));
    }

    /// <summary>
    /// The runs of one job to delete under these limits at <paramref name="now"/>: <paramref name="runs"/> are all the
    /// job's runs, newest first. For stores that hold the runs in memory and the default
    /// <see cref="ICliJobStorageProvider.PruneAsync(string, JobHistoryRetention, DateTimeOffset, CancellationToken)"/>.
    /// </summary>
    public IEnumerable<T> SelectExpired<T>(IReadOnlyList<T> runs, Func<T, JobExecutionStatus> status,
        Func<T, DateTime> startedAt, DateTimeOffset now)
    {
        int failedRank = 0, otherRank = 0;
        for (var index = 0; index < runs.Count; index++)
        {
            var run = runs[index];
            var runStatus = status(run);
            // Every run takes its place in the count, the ones that are never deleted too
            var rank = !CountsFailedSeparately ? index
                : runStatus == JobExecutionStatus.Failed ? failedRank++ : otherRank++;
            if (index == 0 || runStatus == JobExecutionStatus.Running) continue;

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
