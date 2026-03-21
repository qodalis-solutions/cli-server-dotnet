using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugin.DataExplorer.Postgres;

public static class DataExplorerPostgresExtensions
{
    public static CliBuilder AddDataExplorerPostgres(this CliBuilder builder, string connectionString, Action<DataExplorerProviderOptions> configure)
    {
        return builder.AddDataExplorerProvider(new PostgresDataExplorerProvider(connectionString), configure);
    }
}
