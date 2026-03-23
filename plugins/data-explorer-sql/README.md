# Qodalis.Cli.Plugin.DataExplorer.Sql

SQLite data explorer provider for [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet). Enables querying SQLite databases through the Data Explorer UI with full SQL support and automatic schema introspection.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.DataExplorer.Sql
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.DataExplorer.Sql;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddDataExplorerSql("Data Source=./data/app.db", o =>
    {
        o.Name = "my-sqlite";
        o.Description = "Application SQLite database";
    });
});
```

## Schema Introspection

The provider retrieves all tables and views from `sqlite_master` (excluding internal `sqlite_*` tables) along with their column metadata.

## Dependencies

- `Microsoft.Data.Sqlite`

## License

MIT
