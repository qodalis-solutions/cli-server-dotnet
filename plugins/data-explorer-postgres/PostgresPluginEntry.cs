using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.DataExplorer.Postgres.PostgresPluginEntry), Id = "data-explorer-postgres", Order = CliPluginOrder.Source)]

namespace Qodalis.Cli.Plugin.DataExplorer.Postgres;

/// <summary>
/// The PostgreSQL source installed from the dashboard (or <c>cli.AddPlugin&lt;PostgresPluginEntry&gt;(new { ... })</c>): one source of the
/// data explorer (<c>/api/qcli/data-explorer</c>, the dashboard's Data explorer page, the <c>data_*</c> MCP tools), its
/// connection as settings (secrets kept server-side, never returned), read-only by default. Nothing is added until it
/// is configured (the plugin is <c>needs-configuration</c> until then); a change applies after a restart. Loads after
/// the data explorer's own entry.
/// </summary>
public sealed class PostgresPluginEntry : ICliPluginEntry
{
    /// <summary>What the source is: its defaults, connection settings and provider.</summary>
    public static CliDataExplorerSourceSpec Spec(CliPluginContext? context = null) => new()
    {
        Name = "postgres",
        Description = "PostgreSQL database",
        Language = DataExplorerLanguage.Sql,
        OutputFormat = DataExplorerOutputFormat.Table,
        Connection =
        [
            new() { Key = "connectionString", Label = "Connection string", Description = "e.g. Host=db;Database=app;Username=reader;Password=... (kept as a secret, never returned)", Type = CliConfigEntryTypes.Secret, Required = true, RequiresRestart = true },
        ],
        Create = settings => new PostgresDataExplorerProvider(settings.Text("connectionString")),
    };

    /// <summary>Its settings (the marketplace's <c>config</c>; code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings => CliPluginDataExplorerExtensions.SourceSettings(Spec());

    public void Register(CliPluginContext context) =>
        context.AddDataExplorerSource("Data explorer: PostgreSQL", "A PostgreSQL source of the data explorer", Spec(context));
}
