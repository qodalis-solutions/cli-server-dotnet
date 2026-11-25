using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// Options of the jobs plugin as a whole (<see cref="CliBuilderJobExtensions.ConfigureJobs"/>, configuration section
/// <c>Jobs</c>).
/// </summary>
public class CliJobsOptions
{
    /// <summary>The configuration section the options are read from (<c>Jobs:History:MaxAge</c>, ...).</summary>
    public const string SectionName = "Jobs";

    /// <summary>
    /// History retention for every job, off by default (every run is kept). With a limit set here or on a job
    /// (<see cref="CliJobOptions.History"/>), the system job <c>jobs:prune-history</c> deletes the expired runs every
    /// <see cref="CliJobsHistoryOptions.PruneInterval"/>.
    /// </summary>
    public CliJobsHistoryOptions History { get; set; } = new();

    /// <summary>
    /// Keeps the <c>jobs</c> config provider's changes in this store (its own) instead of the server's default one; the
    /// host's store for the provider's id (<c>cli.SetConfigStore(store, "jobs")</c>) still wins. Set in code only.
    /// </summary>
    public ICliConfigStore? ConfigStore { get; set; }
}

/// <summary>The global history retention (<see cref="CliJobsOptions.History"/>) and how often it is applied.</summary>
public class CliJobsHistoryOptions : JobHistoryRetention
{
    /// <summary>How often the <c>jobs:prune-history</c> job runs. Default 1 day.</summary>
    public TimeSpan PruneInterval { get; set; } = TimeSpan.FromDays(1);
}
