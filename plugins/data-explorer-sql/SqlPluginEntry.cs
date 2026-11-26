using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.DataExplorer.Sql.SqlPluginEntry), Id = "data-explorer-sql", Order = CliPluginOrder.Source)]

namespace Qodalis.Cli.Plugin.DataExplorer.Sql;

/// <summary>
/// The SQLite source installed from the dashboard (or <c>cli.AddPlugin&lt;SqlPluginEntry&gt;(new { ... })</c>): one source of the
/// data explorer (<c>/api/qcli/data-explorer</c>, the dashboard's Data explorer page, the <c>data_*</c> MCP tools), its
/// connection as settings (secrets kept server-side, never returned), read-only by default. Nothing is added until it
/// is configured (the plugin is <c>needs-configuration</c> until then); a change applies after a restart. Loads after
/// the data explorer's own entry.
/// </summary>
public sealed class SqlPluginEntry : ICliPluginEntry
{
    /// <summary>What the source is: its defaults, connection settings and provider.</summary>
    public static CliDataExplorerSourceSpec Spec(CliPluginContext? context = null) => new()
    {
        Name = "sql",
        Description = "SQLite database",
        Language = DataExplorerLanguage.Sql,
        OutputFormat = DataExplorerOutputFormat.Table,
        Connection =
        [
            new()
            {
                Key = "filename", Label = "SQLite file", Description = "The SQLite database file (relative paths from the plugin's data directory)",
                Type = CliConfigEntryTypes.String, Default = "data.db", RequiresRestart = true, Validation = new CliConfigValidation { Required = true, Max = 500 },
            },
        ],
        Create = settings => new SqlDataExplorerProvider($"Data Source={(Path.IsPathRooted(settings.Text("filename")) ? settings.Text("filename") : Path.Combine(context?.DataDir ?? ".", settings.Text("filename")))}"),
    };

    /// <summary>Its settings (the marketplace's <c>config</c>; code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings => CliPluginDataExplorerExtensions.SourceSettings(Spec());

    public void Register(CliPluginContext context) =>
        context.AddDataExplorerSource("Data explorer: SQLite", "A SQLite source of the data explorer", Spec(context));
}
