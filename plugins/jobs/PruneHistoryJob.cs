using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// The system job <c>jobs:prune-history</c>: deletes the runs that break the history retention (global or per job).
/// Each run sets the property <c>deleted</c> to how many executions it deleted.
/// </summary>
internal sealed class PruneHistoryJob(CliJobScheduler scheduler) : ICliJob
{
    /// <summary>The job's id and name.</summary>
    public const string JobId = "jobs:prune-history";

    public async Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken)
    {
        var deleted = await scheduler.PruneHistoryAsync(cancellationToken);
        context.Properties["deleted"] = deleted;
        context.Logger.Info($"Deleted {deleted} execution(s)");
    }
}
