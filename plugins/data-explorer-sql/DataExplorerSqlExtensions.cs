using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugin.DataExplorer.Sql;

/// <summary>
/// Extension methods for registering the SQLite data explorer provider with the CLI builder.
/// </summary>
public static class DataExplorerSqlExtensions
{
    /// <summary>
    /// Registers a SQLite data explorer provider with the specified connection string.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="configure">An action to configure the provider options.</param>
    /// <returns>The CLI builder for chaining.</returns>
    public static CliBuilder AddDataExplorerSql(this CliBuilder builder, string connectionString, Action<DataExplorerProviderOptions> configure)
    {
        return builder.AddDataExplorerProvider(new SqlDataExplorerProvider(connectionString), configure);
    }
}
