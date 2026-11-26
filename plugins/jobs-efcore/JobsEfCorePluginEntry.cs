using Qodalis.Cli.Plugin.Databases.EfCore;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.Jobs.EfCore.JobsEfCorePluginEntry), Id = "jobs-efcore")]

namespace Qodalis.Cli.Plugin.Jobs.EfCore;

/// <summary>
/// The jobs' database storage from the dashboard (or <c>cli.AddPlugin&lt;JobsEfCorePluginEntry&gt;(new { dialect = "postgres", connectionString = "..." })</c>):
/// job states, runs and logs kept through EF Core. The dialect, connection (a secret) and table prefix are settings kept
/// in <c>plugin-settings.json</c> and read while the server starts; without a connection it is <c>needs-configuration</c>
/// and runs stay in memory. The dialect's EF Core provider comes from the host or is installed with the plugin.
/// </summary>
public sealed class JobsEfCorePluginEntry : ICliPluginEntry
{
    public void Register(CliPluginContext context)
    {
        var (_, configure, tables) = context.EfStore("Jobs storage", "The database the jobs' runs and logs are kept in", "jobs.db");
        if (configure != null) context.Builder.AddEfCoreJobStorage(configure, o => tables(o));
    }
}
