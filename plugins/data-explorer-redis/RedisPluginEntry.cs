using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.DataExplorer.Redis.RedisPluginEntry), Id = "data-explorer-redis", Order = CliPluginOrder.Source)]

namespace Qodalis.Cli.Plugin.DataExplorer.Redis;

/// <summary>
/// The Redis source installed from the dashboard (or <c>cli.AddPlugin&lt;RedisPluginEntry&gt;(new { ... })</c>): one source of the
/// data explorer (<c>/api/qcli/data-explorer</c>, the dashboard's Data explorer page, the <c>data_*</c> MCP tools), its
/// connection as settings (secrets kept server-side, never returned), read-only by default. Nothing is added until it
/// is configured (the plugin is <c>needs-configuration</c> until then); a change applies after a restart. Loads after
/// the data explorer's own entry.
/// </summary>
public sealed class RedisPluginEntry : ICliPluginEntry
{
    /// <summary>What the source is: its defaults, connection settings and provider.</summary>
    public static CliDataExplorerSourceSpec Spec(CliPluginContext? context = null) => new()
    {
        Name = "redis",
        Description = "Redis database",
        Language = DataExplorerLanguage.Redis,
        OutputFormat = DataExplorerOutputFormat.Json,
        Connection =
        [
            new() { Key = "connectionString", Label = "Connection string", Description = "e.g. host:6379,password=... (kept as a secret, never returned)", Type = CliConfigEntryTypes.Secret, Required = true, RequiresRestart = true },
        ],
        Create = settings => new RedisDataExplorerProvider(settings.Text("connectionString")),
    };

    /// <summary>Its settings (the marketplace's <c>config</c>; code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings => CliPluginDataExplorerExtensions.SourceSettings(Spec());

    public void Register(CliPluginContext context) =>
        context.AddDataExplorerSource("Data explorer: Redis", "A Redis source of the data explorer", Spec(context));
}
