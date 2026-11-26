using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.FileSystem.Sqlite.SqlitePluginEntry), Id = "filesystem-sqlite")]

namespace Qodalis.Cli.Plugin.FileSystem.Sqlite;

/// <summary>
/// The SQLite file storage from the dashboard (or <c>cli.AddPlugin&lt;SqlitePluginEntry&gt;(new { database = "files.db" })</c>):
/// the filesystem API over a SQLite database file (default <c>&lt;data dir&gt;/plugin-data/filesystem-sqlite/files.db</c>),
/// read-only unless <c>readOnly</c> is turned off. Refused when the storage is already set.
/// </summary>
public sealed class SqlitePluginEntry : ICliPluginEntry
{
    /// <summary>Its settings (the marketplace's <c>config</c>; code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings =>
    [
        new()
        {
            Key = "database", Label = "Database file", Description = "The SQLite file (relative paths from the plugin's data directory)",
            Type = CliConfigEntryTypes.String, Default = "files.db", RequiresRestart = true, Validation = new CliConfigValidation { Required = true, Max = 500 },
        },
        new() { Key = "tablePrefix", Label = "Table prefix", Description = "Prepended to the table's name", Type = CliConfigEntryTypes.String, Default = "", RequiresRestart = true },
        FileSystemPluginEntry.ReadOnlySetting("Uploads, new folders and deletes are refused"),
    ];

    public void Register(CliPluginContext context) =>
        context.AddFileStorage(new CliPluginSettingsOptions
        {
            Title = "Filesystem (SQLite)", Description = "Files kept in a SQLite database", Settings = [.. Settings],
        }, "sqlite", settings =>
        {
            var file = settings.Text("database") is { Length: > 0 } f ? f : "files.db";
            var options = new SqliteProviderOptions { DbPath = Path.IsPathRooted(file) || file == ":memory:" ? file : Path.Combine(context.DataDir, file) };
            if (settings.Text("tablePrefix") is { Length: > 0 } prefix) options.TablePrefix = prefix;
            return new SqliteFileStorageProvider(options);
        });
}
