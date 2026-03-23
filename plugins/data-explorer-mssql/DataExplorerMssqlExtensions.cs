using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugin.DataExplorer.Mssql;

/// <summary>
/// Extension methods for registering the Microsoft SQL Server data explorer provider with the CLI builder.
/// </summary>
public static class DataExplorerMssqlExtensions
{
    /// <summary>
    /// Registers a Microsoft SQL Server data explorer provider with the specified connection string.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="connectionString">The MSSQL connection string.</param>
    /// <param name="configure">An action to configure the provider options.</param>
    /// <returns>The CLI builder for chaining.</returns>
    public static CliBuilder AddDataExplorerMssql(this CliBuilder builder, string connectionString, Action<DataExplorerProviderOptions> configure)
    {
        return builder.AddDataExplorerProvider(new MssqlDataExplorerProvider(connectionString), configure);
    }
}
