# Qodalis.Cli.Plugin.DataExplorer.Elasticsearch

Elasticsearch data explorer provider for [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet). Enables querying Elasticsearch clusters through the Data Explorer UI using REST-style queries, search DSL, cat APIs, and index mappings.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.DataExplorer.Elasticsearch
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.DataExplorer.Elasticsearch;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddDataExplorerElasticsearch("http://localhost:9200", o =>
    {
        o.Name = "my-cluster";
        o.Description = "Production Elasticsearch cluster";
    });
});
```

## Query Format

Queries follow the Elasticsearch REST API format. The first line specifies the HTTP method and path, followed by an optional JSON body:

```
GET /my-index/_search
{
  "query": { "match_all": {} },
  "size": 10
}
```

## Dependencies

- `Elastic.Clients.Elasticsearch`

## Terminal commands

None of its own: the provider is queried through the [data explorer](../../README.md#data-explorer-sources)
(`data-explorer` in the web CLI).

## Install from the dashboard or in code

The package declares an entry point (`ElasticsearchPluginEntry`, plugin id `data-explorer-elasticsearch`), so it can be installed from the dashboard's
marketplace (the admin plugin's server plugins, loaded at the next start) or added in code through the same entry:

```csharp
cli.AddPlugin<ElasticsearchPluginEntry>(new { name = "app" });
```

Its settings (config provider `data-explorer-elasticsearch` unless noted; code options are their defaults, saved settings override them):
`name`, `description`, `node` (required secret), `readOnly` (default true), `timeoutMs`, `maxRows`; order Source (70), after the data explorer entry; no source is added until it is configured. A required setting without a value lists the plugin as `needs-configuration` with `missing`; see
[Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard).

## License

MIT
