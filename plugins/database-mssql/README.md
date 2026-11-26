# Qodalis.Cli.Plugin.Database.SqlServer

The SQL Server driver of [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet): `Microsoft.Data.SqlClient` (ADO.NET, for the databases plugin) and `Microsoft.EntityFrameworkCore.SqlServer` (EF Core, for the EF Core stores), in one package, so the
[databases plugin](../databases/README.md) and the EF Core stores (audit-efcore, config-efcore, filesystem-efcore,
jobs-efcore, public-api-efcore, users-efcore) installed from the marketplace can keep their data in SQL Server.

A plugin installed from the marketplace runs in a load context of its own with the assemblies of its own dependencies:
the stores ship their SQLite provider, not a SQL Server driver. Install this plugin next to them: it shares its
folder's assemblies with the other plugins, each of which loads the driver in its own context (bound to its own copy of
EF Core), and registers the `mssql` dialect with one connection they all can use.

## Install from the dashboard

Install **database-mssql** from the dashboard's marketplace (Plugins > Server), set its connection, then restart:

| Setting | Type | Default | |
|---|---|---|---|
| `connectionString` | secret, required | | `Server=localhost;Database=app;User Id=sa;Password=...;TrustServerCertificate=True` |

Without a connection the plugin is `needs-configuration` (`missing: settings.connectionString`); its driver still works
for the plugins that set a connection of their own.

Then set `dialect` to `mssql` on the databases plugin or a store:

- leave its `connectionString` empty to use this plugin's connection (every store keeps its tables apart with its own
  `tablePrefix`), or
- set a connection of its own (another database, other credentials).

A store whose dialect is `mssql` without a connection of its own, while this plugin has none, is
`needs-configuration` (`settings.connectionString or the database-mssql plugin's connection`). Without this plugin the store
fails at start with `the mssql provider is not installed (...: install the database-mssql plugin, or add the package to the host)`.

## In code

A host that references this package has the driver already; the entry only adds the shared connection:

```bash
dotnet add package Qodalis.Cli.Plugin.Database.SqlServer
```

```csharp
using Qodalis.Cli.Plugin.Database.SqlServer;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddPlugin<SqlServerDatabasePluginEntry>(new { connectionString = "Server=localhost;Database=app;User Id=sa;Password=...;TrustServerCertificate=True" });
    cli.AddPlugin<Qodalis.Cli.Plugin.Audit.EfCore.AuditEfCorePluginEntry>(new { dialect = "mssql" });
});
```

The entry (plugin id `database-mssql`, order Driver (1), before every other plugin) is `CliDatabaseDriverPluginEntry` of
`Qodalis.Cli`: the drivers it registers are on `CliBuilder.DatabaseDrivers`. See
[Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard).

## Dependencies

- `Microsoft.EntityFrameworkCore.SqlServer` 10.0.13
- `Microsoft.Data.SqlClient` 6.1.7

## License

MIT
