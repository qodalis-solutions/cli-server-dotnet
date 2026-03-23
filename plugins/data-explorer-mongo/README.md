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

## License

MIT
