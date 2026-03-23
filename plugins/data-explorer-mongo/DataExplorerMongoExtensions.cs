using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugin.DataExplorer.Mongo;

/// <summary>
/// Extension methods for registering the MongoDB data explorer provider with the CLI builder.
/// </summary>
public static class DataExplorerMongoExtensions
{
    /// <summary>
    /// Registers a MongoDB data explorer provider with the specified connection string and database name.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="connectionString">The MongoDB connection string.</param>
    /// <param name="database">The name of the MongoDB database to connect to.</param>
    /// <param name="configure">An action to configure the provider options.</param>
    /// <returns>The CLI builder for chaining.</returns>
    public static CliBuilder AddDataExplorerMongo(this CliBuilder builder, string connectionString, string database, Action<DataExplorerProviderOptions> configure)
    {
        return builder.AddDataExplorerProvider(new MongoDataExplorerProvider(connectionString, database), configure);
    }
}
