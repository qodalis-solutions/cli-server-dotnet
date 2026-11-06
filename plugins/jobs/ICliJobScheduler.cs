using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// The scheduler that runs the jobs registered with <c>AddJob</c> in this process: their registrations and the
/// actions on them. Resolve it to act on a job from the host's own code (e.g. run a job when something happened)
/// without going through the jobs API's <see cref="ICliJobManager"/>, which a host may replace.
/// </summary>
public interface ICliJobScheduler
{
    /// <summary>The registered jobs keyed by job id, with their state (read-only).</summary>
    IReadOnlyDictionary<string, JobRegistration> Jobs { get; }

    /// <summary>
    /// Runs the job now. When <see cref="CliJobOptions.MaxConcurrentRuns"/> runs are going, its
    /// <see cref="CliJobOptions.OverlapPolicy"/> decides: <c>Skip</c> throws <see cref="InvalidOperationException"/>,
    /// <c>Queue</c> queues the run (it starts when a slot frees; at most one waits), <c>Cancel</c> cancels the oldest
    /// run and starts this one in its slot.
    /// </summary>
    Task TriggerAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Like <see cref="TriggerAsync"/>, and tells whether the run started or was queued.</summary>
    async Task<CliJobTriggerOutcome> TriggerWithOutcomeAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await TriggerAsync(jobId, cancellationToken);
        return CliJobTriggerOutcome.Started;
    }

    /// <summary>Pauses the job's schedule.</summary>
    Task PauseAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Resumes a paused job's schedule.</summary>
    Task ResumeAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Stops the job: cancels every running run, drops a queued one and disables its schedule.</summary>
    Task StopJobAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Cancels every running run of the job and drops a queued one.</summary>
    Task CancelCurrentAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Changes the job's options and restarts its schedule.</summary>
    Task UpdateOptionsAsync(string jobId, Action<CliJobOptions> update, CancellationToken cancellationToken = default);
}
