using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.DataExplorer.Mongo.MongoPluginEntry), Id = "data-explorer-mongo", Order = CliPluginOrder.Source)]

namespace Qodalis.Cli.Plugin.DataExplorer.Mongo;

/// <summary>
/// The MongoDB source installed from the dashboard (or <c>cli.AddPlugin&lt;MongoPluginEntry&gt;(new { ... })</c>): one source of the
/// data explorer (<c>/api/qcli/data-explorer</c>, the dashboard's Data explorer page, the <c>data_*</c> MCP tools), its
/// connection as settings (secrets kept server-side, never returned), read-only by default. Nothing is added until it
/// is configured (the plugin is <c>needs-configuration</c> until then); a change applies after a restart. Loads after
/// the data explorer's own entry.
/// </summary>
public sealed class MongoPluginEntry : ICliPluginEntry
{
    /// <summary>What the source is: its defaults, connection settings and provider.</summary>
    public static CliDataExplorerSourceSpec Spec(CliPluginContext? context = null) => new()
    {
        Name = "mongo",
        Description = "MongoDB database",
        Language = DataExplorerLanguage.Json,
        OutputFormat = DataExplorerOutputFormat.Json,
        Connection =
        [
            new() { Key = "connectionString", Label = "Connection string", Description = "e.g. mongodb://user:password@host:27017 (kept as a secret, never returned)", Type = CliConfigEntryTypes.Secret, Required = true, RequiresRestart = true },
            new() { Key = "database", Label = "Database", Description = "The database queried", Type = CliConfigEntryTypes.String, Required = true, Default = "", RequiresRestart = true },
        ],
        Create = settings => new MongoDataExplorerProvider(settings.Text("connectionString"), settings.Text("database")),
    };

    /// <summary>Its settings (the marketplace's <c>config</c>; code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings => CliPluginDataExplorerExtensions.SourceSettings(Spec());

    public void Register(CliPluginContext context) =>
        context.AddDataExplorerSource("Data explorer: MongoDB", "A MongoDB source of the data explorer", Spec(context));
}
