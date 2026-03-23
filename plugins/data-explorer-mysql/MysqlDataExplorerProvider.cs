using MySqlConnector;
using Qodalis.Cli.Abstractions.DataExplorer;

namespace Qodalis.Cli.Plugin.DataExplorer.Mysql;

/// <summary>
/// Data explorer provider for MySQL databases using MySqlConnector.
/// </summary>
public class MysqlDataExplorerProvider : IDataExplorerProvider
{
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="MysqlDataExplorerProvider"/> class.
    /// </summary>
    /// <param name="connectionString">The MySQL connection string.</param>
    public MysqlDataExplorerProvider(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Retrieves the database schema for all tables and views in the current database, including column metadata and primary key information.
    /// </summary>
    /// <param name="options">The provider options containing the data source name.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The schema result containing tables and columns, or <c>null</c> on failure.</returns>
    public async Task<DataExplorerSchemaResult?> GetSchemaAsync(
        DataExplorerProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var tablesCmd = connection.CreateCommand();
        tablesCmd.CommandText = "SELECT TABLE_NAME, TABLE_TYPE FROM information_schema.tables WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME";
        await using var tablesReader = await tablesCmd.ExecuteReaderAsync(cancellationToken);

        var tableNames = new List<(string Name, string Type)>();
        while (await tablesReader.ReadAsync(cancellationToken))
        {
            var tableType = tablesReader.GetString(1).ToUpperInvariant() == "VIEW" ? "view" : "table";
            tableNames.Add((tablesReader.GetString(0), tableType));
        }

        await tablesReader.CloseAsync();

        var tables = new List<DataExplorerSchemaTable>();

        foreach (var (tableName, tableType) in tableNames)
        {
            await using var colCmd = connection.CreateCommand();
            colCmd.CommandText = "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_KEY FROM information_schema.columns WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tableName ORDER BY ORDINAL_POSITION";
            colCmd.Parameters.AddWithValue("@tableName", tableName);
            await using var colReader = await colCmd.ExecuteReaderAsync(cancellationToken);

            var columns = new List<DataExplorerSchemaColumn>();
            while (await colReader.ReadAsync(cancellationToken))
            {
                columns.Add(new DataExplorerSchemaColumn
                {
                    Name = colReader.GetString(0),
                    Type = colReader.GetString(1),
                    Nullable = colReader.GetString(2).ToUpperInvariant() == "YES",
                    PrimaryKey = colReader.GetString(3).ToUpperInvariant() == "PRI"
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
    /// Executes a SQL query against the MySQL database and returns the results.
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

        await using var connection = new MySqlConnection(_connectionString);
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
