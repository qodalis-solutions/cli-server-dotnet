using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.DataExplorer.Elasticsearch.ElasticsearchPluginEntry), Id = "data-explorer-elasticsearch", Order = CliPluginOrder.Source)]

namespace Qodalis.Cli.Plugin.DataExplorer.Elasticsearch;

/// <summary>
/// The Elasticsearch source installed from the dashboard (or <c>cli.AddPlugin&lt;ElasticsearchPluginEntry&gt;(new { ... })</c>): one source of the
/// data explorer (<c>/api/qcli/data-explorer</c>, the dashboard's Data explorer page, the <c>data_*</c> MCP tools), its
/// connection as settings (secrets kept server-side, never returned), read-only by default. Nothing is added until it
/// is configured (the plugin is <c>needs-configuration</c> until then); a change applies after a restart. Loads after
/// the data explorer's own entry.
/// </summary>
public sealed class ElasticsearchPluginEntry : ICliPluginEntry
{
    /// <summary>What the source is: its defaults, connection settings and provider.</summary>
    public static CliDataExplorerSourceSpec Spec(CliPluginContext? context = null) => new()
    {
        Name = "elasticsearch",
        Description = "Elasticsearch database",
        Language = DataExplorerLanguage.Elasticsearch,
        OutputFormat = DataExplorerOutputFormat.Json,
        Connection =
        [
            new() { Key = "node", Label = "Node URL", Description = "e.g. https://user:password@host:9200 (kept as a secret, never returned)", Type = CliConfigEntryTypes.Secret, Required = true, RequiresRestart = true },
        ],
        Create = settings => new ElasticsearchDataExplorerProvider(settings.Text("node")),
    };

    /// <summary>Its settings (the marketplace's <c>config</c>; code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings => CliPluginDataExplorerExtensions.SourceSettings(Spec());

    public void Register(CliPluginContext context) =>
        context.AddDataExplorerSource("Data explorer: Elasticsearch", "An Elasticsearch source of the data explorer", Spec(context));
}
