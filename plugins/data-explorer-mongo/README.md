# Qodalis.Cli.Plugin.DataExplorer.Mongo

MongoDB data explorer provider for [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet). Enables querying MongoDB databases through the Data Explorer UI using shell-style commands such as `db.collection.find({})`, aggregation pipelines, and CRUD operations.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.DataExplorer.Mongo
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.DataExplorer.Mongo;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddDataExplorerMongo(
        "mongodb://localhost:27017",
        "myDatabase",
        o =>
        {
            o.Name = "my-mongo";
            o.Description = "Production MongoDB";
        });
});
```

## Query Format

Queries use MongoDB shell syntax:

```
db.users.find({ age: { $gt: 21 } })
db.orders.aggregate([{ $group: { _id: "$status", count: { $sum: 1 } } }])
show collections
```

## Schema Introspection

The provider automatically infers the schema by listing all collections and sampling one document from each to determine field structure.

## Dependencies

- `MongoDB.Driver`

## Terminal commands

None of its own: the provider is queried through the [data explorer](../../README.md#data-explorer-sources)
(`data-explorer` in the web CLI).

## Install from the dashboard or in code

The package declares an entry point (`MongoPluginEntry`, plugin id `data-explorer-mongo`), so it can be installed from the dashboard's
marketplace (the admin plugin's server plugins, loaded at the next start) or added in code through the same entry:

```csharp
cli.AddPlugin<MongoPluginEntry>(new { name = "app" });
```

Its settings (config provider `data-explorer-mongo` unless noted; code options are their defaults, saved settings override them):
`name`, `description`, `connectionString` (required secret), `database` (required), `readOnly` (default true), `timeoutMs`, `maxRows`; order Source (70), after the data explorer entry; no source is added until it is configured. A required setting without a value lists the plugin as `needs-configuration` with `missing`; see
[Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard).

## License

MIT
