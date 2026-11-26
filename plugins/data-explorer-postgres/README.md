# Qodalis.Cli.Plugin.DataExplorer.Postgres

PostgreSQL data explorer provider for [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet). Enables querying PostgreSQL databases through the Data Explorer UI with full SQL support and automatic schema introspection.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.DataExplorer.Postgres
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.DataExplorer.Postgres;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddDataExplorerPostgres(
        "Host=localhost;Database=mydb;Username=postgres;Password=...",
        o =>
        {
            o.Name = "my-postgres";
            o.Description = "Production PostgreSQL";
        });
});
```

## Schema Introspection

The provider retrieves all tables and views in the `public` schema along with their column metadata (name, type, nullability).

## Dependencies

- `Npgsql`

## Terminal commands

None of its own: the provider is queried through the [data explorer](../../README.md#data-explorer-sources)
(`data-explorer` in the web CLI).

## Install from the dashboard or in code

The package declares an entry point (`PostgresPluginEntry`, plugin id `data-explorer-postgres`), so it can be installed from the dashboard's
marketplace (the admin plugin's server plugins, loaded at the next start) or added in code through the same entry:

```csharp
cli.AddPlugin<PostgresPluginEntry>(new { name = "app" });
```

Its settings (config provider `data-explorer-postgres` unless noted; code options are their defaults, saved settings override them):
`name`, `description`, `connectionString` (required secret), `readOnly` (default true), `timeoutMs`, `maxRows`; order Source (70), after the data explorer entry; no source is added until it is configured. A required setting without a value lists the plugin as `needs-configuration` with `missing`; see
[Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard).

## License

MIT
