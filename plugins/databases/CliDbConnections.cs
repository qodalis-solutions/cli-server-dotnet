using System.Data.Common;
using Qodalis.Cli.Plugins;

namespace Qodalis.Cli.Plugin.Databases;

/// <summary>
/// An ADO.NET connection of a dialect, its driver found at runtime (the host's package, the driver plugin's:
/// database-postgres, database-mssql, or one installed with the plugin): sqlite (<c>Microsoft.Data.Sqlite</c>), postgres (<c>Npgsql</c>), mysql (<c>MySqlConnector</c>) and mssql
/// (<c>Microsoft.Data.SqlClient</c>).
/// </summary>
public static class CliDbConnections
{
    /// <summary>The dialects.</summary>
    public static readonly IReadOnlyList<string> Dialects = ["postgres", "mysql", "mssql", "sqlite"];

    private static readonly Dictionary<string, (string Assembly, string Type)> Drivers = new()
    {
        ["sqlite"] = ("Microsoft.Data.Sqlite", "Microsoft.Data.Sqlite.SqliteConnection"),
        ["postgres"] = ("Npgsql", "Npgsql.NpgsqlConnection"),
        ["mysql"] = ("MySqlConnector", "MySqlConnector.MySqlConnection"),
        ["mssql"] = ("Microsoft.Data.SqlClient", "Microsoft.Data.SqlClient.SqlConnection"),
    };

    /// <summary>A new connection (not opened). Throws <see cref="InvalidOperationException"/> for an unknown dialect or a missing driver.</summary>
    public static DbConnection Create(string dialect, string connectionString)
    {
        if (!Drivers.TryGetValue(dialect, out var d)) throw new InvalidOperationException($"unknown dialect: {dialect} ({string.Join(", ", Dialects)})");
        // The copy this plugin's load context sees, never another plugin's
        var assembly = Qodalis.Cli.Plugins.CliPluginLoadContext.FindAssembly(d.Assembly, typeof(CliDbConnections).Assembly);
        var type = assembly?.GetType(d.Type)
            ?? throw new InvalidOperationException($"the {dialect} driver is not installed ({CliDatabaseDrivers.InstallHint(dialect, d.Assembly)})");
        return (DbConnection)Activator.CreateInstance(type, connectionString)!;
    }
}
