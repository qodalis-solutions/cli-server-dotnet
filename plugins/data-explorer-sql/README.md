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

## Terminal commands

None of its own: the provider is queried through the [data explorer](../../README.md#data-explorer-sources)
(`data-explorer` in the web CLI).

## Install from the dashboard or in code

The package declares an entry point (`SqlPluginEntry`, plugin id `data-explorer-sql`), so it can be installed from the dashboard's
marketplace (the admin plugin's server plugins, loaded at the next start) or added in code through the same entry:

```csharp
cli.AddPlugin<SqlPluginEntry>(new { name = "app" });
```

Its settings (config provider `data-explorer-sql` unless noted; code options are their defaults, saved settings override them):
`name`, `description`, `filename` (relative to the plugin's data folder), `readOnly` (default true), `timeoutMs`, `maxRows`; order Source (70), after the data explorer entry; no source is added until it is configured. A required setting without a value lists the plugin as `needs-configuration` with `missing`; see
[Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard).

## License

MIT
