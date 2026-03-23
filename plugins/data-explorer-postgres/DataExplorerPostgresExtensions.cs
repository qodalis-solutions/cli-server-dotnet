using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugin.DataExplorer.Postgres;

/// <summary>
/// Extension methods for registering the PostgreSQL data explorer provider with the CLI builder.
/// </summary>
public static class DataExplorerPostgresExtensions
{
    /// <summary>
    /// Registers a PostgreSQL data explorer provider with the specified connection string.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    /// <param name="configure">An action to configure the provider options.</param>
    /// <returns>The CLI builder for chaining.</returns>
    public static CliBuilder AddDataExplorerPostgres(this CliBuilder builder, string connectionString, Action<DataExplorerProviderOptions> configure)
    {
        return builder.AddDataExplorerProvider(new PostgresDataExplorerProvider(connectionString), configure);
    }
}
