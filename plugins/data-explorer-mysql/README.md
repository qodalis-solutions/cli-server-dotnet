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

## License

MIT
