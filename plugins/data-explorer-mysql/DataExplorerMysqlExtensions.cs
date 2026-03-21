using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugin.DataExplorer.Mysql;

public static class DataExplorerMysqlExtensions
{
    public static CliBuilder AddDataExplorerMysql(this CliBuilder builder, string connectionString, Action<DataExplorerProviderOptions> configure)
    {
        return builder.AddDataExplorerProvider(new MysqlDataExplorerProvider(connectionString), configure);
    }
}
