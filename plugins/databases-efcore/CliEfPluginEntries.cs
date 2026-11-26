using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Plugins;

namespace Qodalis.Cli.Plugin.Databases.EfCore;

/// <summary>
/// The EF Core provider of a dialect, found at runtime (the host's package, the driver plugin's: database-postgres,
/// database-mssql, or one installed with the plugin): sqlite
/// (<c>Microsoft.EntityFrameworkCore.Sqlite</c>), postgres (<c>Npgsql.EntityFrameworkCore.PostgreSQL</c>) and mssql
/// (<c>Microsoft.EntityFrameworkCore.SqlServer</c>).
/// </summary>
public static class CliEfDialects
{
    /// <summary>The dialects an entry's <c>dialect</c> setting accepts.</summary>
    public static readonly IReadOnlyList<string> All = ["sqlite", "postgres", "mssql"];

    private static readonly Dictionary<string, (string Assembly, string Type, string Method)> Providers = new()
    {
        ["sqlite"] = ("Microsoft.EntityFrameworkCore.Sqlite", "Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions", "UseSqlite"),
        ["postgres"] = ("Npgsql.EntityFrameworkCore.PostgreSQL", "Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilderExtensions", "UseNpgsql"),
        ["mssql"] = ("Microsoft.EntityFrameworkCore.SqlServer", "Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions", "UseSqlServer"),
    };

    /// <summary>
    /// Points <paramref name="options"/> at <paramref name="connectionString"/> with the dialect's provider. Throws
    /// <see cref="InvalidOperationException"/> for an unknown dialect or a provider that is not installed.
    /// </summary>
    public static void Use(DbContextOptionsBuilder options, string dialect, string connectionString) =>
        UseMethod(dialect).Invoke(null, [options, connectionString, null]);

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> for an unknown dialect or a provider that is not installed. The
    /// entries check it while they register, so a missing provider fails the plugin instead of the server's start
    /// (the context's options are only built when a store is first resolved).
    /// </summary>
    public static void EnsureInstalled(string dialect) => UseMethod(dialect);

    private static MethodInfo UseMethod(string dialect)
    {
        if (!Providers.TryGetValue(dialect, out var p)) throw new InvalidOperationException($"unknown dialect: {dialect} ({string.Join(", ", All)})");
        // The copy this plugin's load context sees: another plugin's copy has other EF Core types
        var assembly = CliPluginLoadContext.FindAssembly(p.Assembly, typeof(CliEfDialects).Assembly);
        return assembly?.GetType(p.Type)?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == p.Method && !m.IsGenericMethod && m.GetParameters() is var ps && ps.Length == 3
                && ps[0].ParameterType == typeof(DbContextOptionsBuilder) && ps[1].ParameterType == typeof(string))
            ?? throw new InvalidOperationException($"the {dialect} provider is not installed ({CliDatabaseDrivers.InstallHint(dialect, p.Assembly)})");
    }

}

/// <summary>The database settings of the EF Core stores' entries (users tokens, audit, jobs, config, files, public API usage).</summary>
public static class CliEfPluginEntries
{
    /// <summary>
    /// dialect, connectionString (a secret; for SQLite a file, relative to the plugin's data directory; empty: the
    /// dialect's driver plugin's connection) and tablePrefix.
    /// </summary>
    public static List<CliConfigSettingDefinition> DatabaseSettings(string sqliteFile) =>
    [
        new()
        {
            Key = "dialect", Label = "Dialect", Type = CliConfigEntryTypes.Enum, Default = "sqlite", RequiresRestart = true,
            Validation = new CliConfigValidation { Options = [.. CliEfDialects.All] },
        },
        new()
        {
            Key = "connectionString", Label = "Connection",
            Description = $"A connection string (postgres, mssql), or the SQLite file (relative: the plugin data folder, e.g. {sqliteFile}); kept as a secret. Empty: the connection of the dialect's driver plugin (database-postgres, database-mssql)",
            Type = CliConfigEntryTypes.Secret, RequiresRestart = true,
        },
        new()
        {
            Key = "tablePrefix", Label = "Table prefix", Description = "Prepended to the tables' names", Type = CliConfigEntryTypes.String,
            Default = QcliTableNames.DefaultPrefix, RequiresRestart = true, Validation = new CliConfigValidation { Max = 30 },
        },
    ];

    /// <summary>
    /// Registers an EF Core store's settings (kept in <c>plugin-settings.json</c>, read now: the store is set up while
    /// the services are registered) and returns how to configure its context, or null while the connection is missing:
    /// neither its own nor the dialect's driver plugin's (the plugin is then <c>needs-configuration</c>).
    /// </summary>
    public static (CliStoredConfigProvider Settings, Action<DbContextOptionsBuilder>? Configure, Action<QcliTableNameOptions> Tables) EfStore(
        this CliPluginContext context, string title, string description, string sqliteFile, IEnumerable<CliConfigSettingDefinition>? more = null)
    {
        var settings = context.BootSettings(new CliPluginSettingsOptions
        {
            Title = title, Description = description, Settings = [.. DatabaseSettings(sqliteFile), .. more ?? []],
        });
        var dialect = settings.Text("dialect") is { Length: > 0 } d ? d : "sqlite";
        // Its own connection, else the one of the dialect's driver plugin (installed before it: CliPluginOrder.Driver)
        var connection = settings.Text("connectionString") is { Length: > 0 } own ? own : context.Builder.DatabaseDrivers.ConnectionString(dialect) ?? "";
        var prefix = settings.Text("tablePrefix");
        void Tables(QcliTableNameOptions o)
        {
            if (prefix.Length > 0) o.TablePrefix = prefix;
        }
        if (connection.Length == 0)
        {
            context.NeedsConfiguration(CliDatabaseDrivers.MissingConnection(dialect));
            return (settings, null, Tables);
        }
        // A missing provider fails this plugin now, not the server's start when the store is first resolved
        CliEfDialects.EnsureInstalled(dialect);
        if (dialect == "sqlite" && !connection.Contains('='))
        {
            var file = Path.IsPathRooted(connection) || connection == ":memory:" ? connection : Path.Combine(context.DataDir, connection);
            connection = $"Data Source={file}";
        }
        return (settings, o => CliEfDialects.Use(o, dialect, connection), Tables);
    }
}
