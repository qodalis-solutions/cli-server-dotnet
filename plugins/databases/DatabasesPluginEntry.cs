using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Plugins;

[assembly: Qodalis.Cli.Plugins.CliPlugin(typeof(Qodalis.Cli.Plugin.Databases.DatabasesPluginEntry), Id = "databases", Order = CliPluginOrder.Core)]

namespace Qodalis.Cli.Plugin.Databases;

/// <summary>
/// The databases plugin from the dashboard (or <c>cli.AddPlugin&lt;DatabasesPluginEntry&gt;(new { dialect = "sqlite", connectionString = "app.db" })</c>):
/// one database from its settings (name, dialect, connection a secret, migrations folder, apply on startup), shown with
/// its size, load, largest tables and migrations on the dashboard, in the terminal (<c>databases</c>), over MCP and at
/// <c>/api/v1/qcli/databases</c>. Loads at order Core, before the plugins that keep their data in it. The settings are
/// kept in <c>plugin-settings.json</c> and read while the server starts; without a connection it is
/// <c>needs-configuration</c>. The dialect's driver comes from the host or from its driver plugin (database-postgres,
/// database-mssql), whose connection it uses when it has none of its own.
/// </summary>
public sealed class DatabasesPluginEntry : ICliPluginEntry
{
    /// <summary>Its settings (code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings =>
    [
        new()
        {
            Key = "name", Label = "Name", Description = "The database in the API, the terminal and the dashboard", Type = CliConfigEntryTypes.String,
            Default = "main", RequiresRestart = true, Validation = new CliConfigValidation { Required = true, Pattern = "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$" },
        },
        new()
        {
            Key = "dialect", Label = "Dialect", Type = CliConfigEntryTypes.Enum, Default = "postgres", RequiresRestart = true,
            Validation = new CliConfigValidation { Options = [.. CliDbConnections.Dialects] },
        },
        new()
        {
            Key = "connectionString", Label = "Connection",
            Description = "A connection string, or the SQLite file (relative: the plugin data folder); kept as a secret. Empty: the connection of the dialect's driver plugin (database-postgres, database-mssql)",
            Type = CliConfigEntryTypes.Secret, RequiresRestart = true,
        },
        new()
        {
            Key = "migrationsFolder", Label = "Migrations folder", Description = "NNNN_name.up.sql / .down.sql files (relative: the plugin data folder); empty: none",
            Type = CliConfigEntryTypes.String, Default = "", RequiresRestart = true, Validation = new CliConfigValidation { Max = 500 },
        },
        new()
        {
            Key = "applyOnStartup", Label = "Apply on startup", Description = "Pending migrations run when the server starts",
            Type = CliConfigEntryTypes.Boolean, Default = false, RequiresRestart = true,
        },
    ];

    public void Register(CliPluginContext context)
    {
        var settings = context.BootSettings(new CliPluginSettingsOptions
        {
            Title = "Databases", Description = "The database the databases plugin shows and migrates", Settings = [.. Settings],
        });
        var name = settings.Text("name") is { Length: > 0 } n ? n : "main";
        var dialect = settings.Text("dialect") is { Length: > 0 } d ? d : "postgres";
        // Its own connection, else the one of the dialect's driver plugin (installed before it: CliPluginOrder.Driver)
        var connection = settings.Text("connectionString") is { Length: > 0 } own ? own : context.Builder.DatabaseDrivers.ConnectionString(dialect) ?? "";
        if (connection.Length == 0)
        {
            context.NeedsConfiguration(CliDatabaseDrivers.MissingConnection(dialect));
            return;
        }
        if (dialect == "sqlite" && !connection.Contains('='))
            connection = $"Data Source={(Path.IsPathRooted(connection) || connection == ":memory:" ? connection : Path.Combine(context.DataDir, connection))}";
        var folder = settings.Text("migrationsFolder");
        var apply = settings.Flag("applyOnStartup", false);
        context.Builder.AddDatabases(b =>
        {
            b.AddDatabase(name, db =>
            {
                db.UseConnection(_ => CliDbConnections.Create(dialect, connection));
                if (apply) db.ApplyMigrationsOnStartup();
            });
            if (folder.Length > 0)
            {
                var path = Path.IsPathRooted(folder) ? folder : Path.Combine(context.DataDir, folder);
                Directory.CreateDirectory(path);
                b.AddSqlFolder(name, path);
            }
        });
    }
}
