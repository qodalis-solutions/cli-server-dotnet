# Qodalis.Cli.Plugin.DataExplorer.Mysql

MySQL data explorer provider for [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet). Enables querying MySQL databases through the Data Explorer UI with full SQL support and automatic schema introspection.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.DataExplorer.Mysql
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.DataExplorer.Mysql;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddDataExplorerMysql(
        "Server=localhost;Database=mydb;User=root;Password=...",
        o =>
        {
            o.Name = "my-mysql";
            o.Description = "Production MySQL";
        });
});
```

## Schema Introspection

The provider retrieves all tables and views in the current database along with their column metadata (name, type, nullability, primary key status).

## Dependencies

- `MySqlConnector`

## Terminal commands

None of its own: the provider is queried through the [data explorer](../../README.md#data-explorer-sources)
(`data-explorer` in the web CLI).

## Install from the dashboard or in code

The package declares an entry point (`MysqlPluginEntry`, plugin id `data-explorer-mysql`), so it can be installed from the dashboard's
marketplace (the admin plugin's server plugins, loaded at the next start) or added in code through the same entry:

```csharp
cli.AddPlugin<MysqlPluginEntry>(new { name = "app" });
```

Its settings (config provider `data-explorer-mysql` unless noted; code options are their defaults, saved settings override them):
`name`, `description`, `connectionString` (required secret), `readOnly` (default true), `timeoutMs`, `maxRows`; order Source (70), after the data explorer entry; no source is added until it is configured. A required setting without a value lists the plugin as `needs-configuration` with `missing`; see
[Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard).

## License

MIT
