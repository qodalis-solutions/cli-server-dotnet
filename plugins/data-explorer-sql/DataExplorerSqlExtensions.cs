using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugin.DataExplorer.Sql;

public static class DataExplorerSqlExtensions
{
    public static CliBuilder AddDataExplorerSql(this CliBuilder builder, string connectionString, Action<DataExplorerProviderOptions> configure)
    {
        return builder.AddDataExplorerProvider(new SqlDataExplorerProvider(connectionString), configure);
    }
}
