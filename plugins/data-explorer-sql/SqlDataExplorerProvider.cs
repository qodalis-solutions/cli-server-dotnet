using Microsoft.Data.Sqlite;
using Qodalis.Cli.Abstractions.DataExplorer;

namespace Qodalis.Cli.Plugin.DataExplorer.Sql;

/// <summary>
/// Data explorer provider for SQLite databases using Microsoft.Data.Sqlite.
/// </summary>
public class SqlDataExplorerProvider : IDataExplorerProvider
{
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlDataExplorerProvider"/> class.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    public SqlDataExplorerProvider(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Retrieves the database schema including all tables, views, and their columns.
    /// </summary>
    /// <param name="options">The provider options containing the data source name.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The schema result containing tables and columns, or <c>null</c> on failure.</returns>
    public async Task<DataExplorerSchemaResult?> GetSchemaAsync(
        DataExplorerProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var tablesCmd = connection.CreateCommand();
        tablesCmd.CommandText = "SELECT name, type FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name";
        await using var tablesReader = await tablesCmd.ExecuteReaderAsync(cancellationToken);

        var tables = new List<DataExplorerSchemaTable>();
        var tableNames = new List<(string Name, string Type)>();
        while (await tablesReader.ReadAsync(cancellationToken))
        {
            tableNames.Add((tablesReader.GetString(0), tablesReader.GetString(1)));
        }

        foreach (var (tableName, tableType) in tableNames)
        {
            await using var colCmd = connection.CreateCommand();
            colCmd.CommandText = $"PRAGMA table_info(\"{tableName}\")";
            await using var colReader = await colCmd.ExecuteReaderAsync(cancellationToken);

            var columns = new List<DataExplorerSchemaColumn>();
            while (await colReader.ReadAsync(cancellationToken))
            {
                columns.Add(new DataExplorerSchemaColumn
                {
                    Name = colReader.GetString(1),
                    Type = colReader.IsDBNull(2) ? "TEXT" : colReader.GetString(2),
                    Nullable = colReader.GetInt32(3) == 0,
                    PrimaryKey = colReader.GetInt32(5) > 0
                });
            }

            tables.Add(new DataExplorerSchemaTable
            {
                Name = tableName,
                Type = tableType,
                Columns = columns
            });
        }

        return new DataExplorerSchemaResult
        {
            Source = options.Name,
            Tables = tables
        };
    }

    /// <summary>
    /// Executes a SQL query against the SQLite database and returns the results.
    /// </summary>
    /// <param name="context">The execution context containing the query, parameters, and options.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The query result containing columns, rows, and metadata.</returns>
    public async Task<DataExplorerResult> ExecuteAsync(
        DataExplorerExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var result = new DataExplorerResult
        {
            Success = true,
            Source = context.Options.Name,
            Language = context.Options.Language,
            DefaultOutputFormat = context.Options.DefaultOutputFormat
        };

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = context.Query;

        foreach (var param in context.Parameters)
        {
            command.Parameters.AddWithValue($"@{param.Key}", param.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var columns = new List<string>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(reader.GetName(i));
        }

        result.Columns = columns;

        var rows = new List<object>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new object[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? null! : reader.GetValue(i);
            }

            rows.Add(row);
        }

        result.Rows = rows;
        result.RowCount = rows.Count;

        return result;
    }
}
