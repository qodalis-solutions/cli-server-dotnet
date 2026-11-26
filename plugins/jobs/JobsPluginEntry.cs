using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.Jobs.JobsPluginEntry), Id = "jobs")]

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// The jobs plugin from the dashboard (or <c>cli.AddPlugin&lt;JobsPluginEntry&gt;()</c>): the job scheduler, its API
/// (<c>/api/v1/qcli/jobs</c>), the <c>jobs_*</c> MCP tools and the dashboard's Jobs page. Its settings are its own
/// <c>jobs</c> config provider (run history retention). Jobs come from other plugins and the host's code; runs are kept
/// in memory unless jobs-efcore (or the host) sets a storage.
/// </summary>
public sealed class JobsPluginEntry : ICliPluginEntry
{
    public void Register(CliPluginContext context) => context.Builder.ConfigureJobs(_ => { });
}
