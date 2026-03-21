using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugin.DataExplorer.Mssql;

public static class DataExplorerMssqlExtensions
{
    public static CliBuilder AddDataExplorerMssql(this CliBuilder builder, string connectionString, Action<DataExplorerProviderOptions> configure)
    {
        return builder.AddDataExplorerProvider(new MssqlDataExplorerProvider(connectionString), configure);
    }
}
