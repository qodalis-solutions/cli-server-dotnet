using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugin.DataExplorer.Mongo;

public static class DataExplorerMongoExtensions
{
    public static CliBuilder AddDataExplorerMongo(this CliBuilder builder, string connectionString, string database, Action<DataExplorerProviderOptions> configure)
    {
        return builder.AddDataExplorerProvider(new MongoDataExplorerProvider(connectionString, database), configure);
    }
}
