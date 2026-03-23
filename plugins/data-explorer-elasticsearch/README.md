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

## License

MIT
