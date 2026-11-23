using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Qodalis.Cli.Plugin.Databases;

/// <summary>The SQL dialects the databases plugin knows.</summary>
public static class CliSqlDialects
{
    public const string Postgres = "postgres";
    public const string MySql = "mysql";
    public const string SqlServer = "mssql";
    public const string Sqlite = "sqlite";

    /// <summary>The dialect of an ADO.NET connection, by its type (Npgsql, MySqlConnector/MySql.Data, SqlClient, Microsoft.Data.Sqlite).</summary>
    /// <exception cref="NotSupportedException">Another database.</exception>
    public static string Of(DbConnection connection) => connection.GetType().Name switch
    {
        "NpgsqlConnection" => Postgres,
        "MySqlConnection" => MySql,
        "SqlConnection" => SqlServer,
        "SqliteConnection" => Sqlite,
        var name => throw new NotSupportedException($"unsupported database connection: {name}"),
    };
}

/// <summary>
/// An open connection a migration run works on: the lock, the history and the steps all use it. SQL takes <c>?</c>
/// placeholders, bound in order.
/// </summary>
public sealed partial class CliSqlSession
{
    internal CliSqlSession(DbConnection connection, string dialect, IServiceProvider services, CancellationToken cancellationToken)
    {
        Connection = connection;
        Dialect = dialect;
        Services = services;
        CancellationToken = cancellationToken;
    }

    /// <summary>The open connection.</summary>
    public DbConnection Connection { get; }

    /// <summary><see cref="CliSqlDialects"/>: <c>postgres</c>, <c>mysql</c>, <c>mssql</c> or <c>sqlite</c>.</summary>
    public string Dialect { get; }

    /// <summary>The transaction the current step runs in, if any (commands of the session join it).</summary>
    public DbTransaction? Transaction { get; internal set; }

    /// <summary>The application's services (code steps resolve what they need).</summary>
    public IServiceProvider Services { get; }

    /// <summary>Cancelled when the run is.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Runs a statement; returns the rows affected.</summary>
    public async Task<int> ExecuteAsync(string sql, params object?[] args)
    {
        await using var command = Command(sql, args);
        return await command.ExecuteNonQueryAsync(CancellationToken);
    }

    /// <summary>Runs a query; every row as column name → value (<c>DBNull</c> as null).</summary>
    public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, params object?[] args)
    {
        await using var command = Command(sql, args);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(CancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Runs a script of several statements (SQL Server: batches separated by <c>GO</c> lines).</summary>
    public async Task ExecuteScriptAsync(string script)
    {
        var batches = Dialect == CliSqlDialects.SqlServer ? GoSeparator().Split(script) : [script];
        foreach (var batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var command = Connection.CreateCommand();
            command.Transaction = Transaction;
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync(CancellationToken);
        }
    }

    [GeneratedRegex(@"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();

    private DbCommand Command(string sql, object?[] args)
    {
        var command = Connection.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = NumberPlaceholders(sql, args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@p" + i.ToString(CultureInfo.InvariantCulture);
            parameter.Value = Value(args[i]);
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private object Value(object? value) => value switch
    {
        null => DBNull.Value,
        // SQLite keeps times as ISO text; the others get UTC times
        DateTimeOffset d when Dialect == CliSqlDialects.Sqlite => d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        DateTimeOffset d when Dialect == CliSqlDialects.Postgres => d.UtcDateTime,
        DateTimeOffset d => DateTime.SpecifyKind(d.UtcDateTime, DateTimeKind.Unspecified),
        bool b when Dialect == CliSqlDialects.Sqlite => b ? 1 : 0,
        _ => value,
    };

    /// <summary>Replaces the <c>?</c> placeholders outside quotes with <c>@p0</c>, <c>@p1</c>, ...</summary>
    internal static string NumberPlaceholders(string sql, int count)
    {
        if (count == 0) return sql;
        var builder = new StringBuilder(sql.Length + count * 3);
        var n = 0;
        char? quote = null;
        foreach (var c in sql)
        {
            if (quote != null)
            {
                if (c == quote) quote = null;
                builder.Append(c);
            }
            else if (c is '\'' or '"' or '`')
            {
                quote = c;
                builder.Append(c);
            }
            else if (c == '?' && n < count)
            {
                builder.Append("@p").Append(n++.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    /// <summary>A time read from a history table, as ISO 8601 UTC.</summary>
    internal static string? Iso(object? value) => value switch
    {
        null => null,
        DateTime d => DateTime.SpecifyKind(d, d.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : d.Kind)
            .ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) =>
            d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    internal async Task EnsureOpenAsync()
    {
        if (Connection.State != ConnectionState.Open) await Connection.OpenAsync(CancellationToken);
    }
}
