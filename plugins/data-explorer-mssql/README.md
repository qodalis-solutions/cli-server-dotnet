# Qodalis.Cli.Plugin.DataExplorer.Mssql

Microsoft SQL Server data explorer provider for [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet). Enables querying SQL Server databases through the Data Explorer UI with full SQL support and automatic schema introspection.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.DataExplorer.Mssql
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.DataExplorer.Mssql;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddDataExplorerMssql(
        "Server=localhost;Database=MyDb;User Id=sa;Password=...;TrustServerCertificate=True",
        o =>
        {
            o.Name = "my-sqlserver";
            o.Description = "Production SQL Server";
        });
});
```

## Schema Introspection

The provider retrieves all tables and views in the `dbo` schema along with their column metadata (name, type, nullability, primary key status).

## Dependencies

- `Microsoft.Data.SqlClient`

## License

MIT
