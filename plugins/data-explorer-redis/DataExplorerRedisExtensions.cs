using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugin.DataExplorer.Redis;

public static class DataExplorerRedisExtensions
{
    public static CliBuilder AddDataExplorerRedis(this CliBuilder builder, string connectionString, Action<DataExplorerProviderOptions> configure)
    {
        return builder.AddDataExplorerProvider(new RedisDataExplorerProvider(connectionString), configure);
    }
}
