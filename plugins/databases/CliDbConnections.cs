using System.Data.Common;
using System.Reflection;
using System.Runtime.Loader;

namespace Qodalis.Cli.Plugin.Databases;

/// <summary>
/// An ADO.NET connection of a dialect, its driver found at runtime (the host's package, or one installed with the
/// plugin): sqlite (<c>Microsoft.Data.Sqlite</c>), postgres (<c>Npgsql</c>), mysql (<c>MySqlConnector</c>) and mssql
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
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == d.Assembly) ?? Load(d.Assembly);
        var type = assembly?.GetType(d.Type)
            ?? throw new InvalidOperationException($"the {dialect} driver is not installed ({d.Assembly}: add it to the host, or install it with the plugin)");
        return (DbConnection)Activator.CreateInstance(type, connectionString)!;
    }

    private static Assembly? Load(string name)
    {
        try
        {
            var context = AssemblyLoadContext.GetLoadContext(typeof(CliDbConnections).Assembly) ?? AssemblyLoadContext.Default;
            return context.LoadFromAssemblyName(new AssemblyName(name));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }
}
