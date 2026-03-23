using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugin.DataExplorer.Elasticsearch;

/// <summary>
/// Extension methods for registering the Elasticsearch data explorer provider with the CLI builder.
/// </summary>
public static class DataExplorerElasticsearchExtensions
{
    /// <summary>
    /// Registers an Elasticsearch data explorer provider with the specified node URL.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="node">The Elasticsearch node URL (e.g., <c>http://localhost:9200</c>).</param>
    /// <param name="configure">An action to configure the provider options.</param>
    /// <returns>The CLI builder for chaining.</returns>
    public static CliBuilder AddDataExplorerElasticsearch(this CliBuilder builder, string node, Action<DataExplorerProviderOptions> configure)
    {
        return builder.AddDataExplorerProvider(new ElasticsearchDataExplorerProvider(node), configure);
    }
}
