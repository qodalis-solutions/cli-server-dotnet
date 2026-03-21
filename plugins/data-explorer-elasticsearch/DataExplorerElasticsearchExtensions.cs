using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugin.DataExplorer.Elasticsearch;

public static class DataExplorerElasticsearchExtensions
{
    public static CliBuilder AddDataExplorerElasticsearch(this CliBuilder builder, string node, Action<DataExplorerProviderOptions> configure)
    {
        return builder.AddDataExplorerProvider(new ElasticsearchDataExplorerProvider(node), configure);
    }
}
