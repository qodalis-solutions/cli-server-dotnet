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

## License

MIT
