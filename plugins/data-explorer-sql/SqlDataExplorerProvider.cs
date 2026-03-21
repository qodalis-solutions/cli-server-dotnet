using Microsoft.Data.Sqlite;
using Qodalis.Cli.Abstractions.DataExplorer;

namespace Qodalis.Cli.Plugin.DataExplorer.Sql;

public class SqlDataExplorerProvider : IDataExplorerProvider
{
    private readonly string _connectionString;

    public SqlDataExplorerProvider(string connectionString)
    {
        _connectionString = connectionString;
    }

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
