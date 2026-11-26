# Qodalis.Cli.Plugin.Database.Postgres

The PostgreSQL driver of [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet): `Npgsql` (ADO.NET, for the databases plugin) and `Npgsql.EntityFrameworkCore.PostgreSQL` (EF Core, for the EF Core stores), in one package, so the
[databases plugin](../databases/README.md) and the EF Core stores (audit-efcore, config-efcore, filesystem-efcore,
jobs-efcore, public-api-efcore, users-efcore) installed from the marketplace can keep their data in PostgreSQL.

A plugin installed from the marketplace runs in a load context of its own with the assemblies of its own dependencies:
the stores ship their SQLite provider, not a PostgreSQL driver. Install this plugin next to them: it shares its
folder's assemblies with the other plugins, each of which loads the driver in its own context (bound to its own copy of
EF Core), and registers the `postgres` dialect with one connection they all can use.

## Install from the dashboard

Install **database-postgres** from the dashboard's marketplace (Plugins > Server), set its connection, then restart:

| Setting | Type | Default | |
|---|---|---|---|
| `connectionString` | secret, required | | `Host=localhost;Database=app;Username=app;Password=...` |

Without a connection the plugin is `needs-configuration` (`missing: settings.connectionString`); its driver still works
for the plugins that set a connection of their own.

Then set `dialect` to `postgres` on the databases plugin or a store:

- leave its `connectionString` empty to use this plugin's connection (every store keeps its tables apart with its own
  `tablePrefix`), or
- set a connection of its own (another database, other credentials).

A store whose dialect is `postgres` without a connection of its own, while this plugin has none, is
`needs-configuration` (`settings.connectionString or the database-postgres plugin's connection`). Without this plugin the store
fails at start with `the postgres provider is not installed (...: install the database-postgres plugin, or add the package to the host)`.

## In code

A host that references this package has the driver already; the entry only adds the shared connection:

```bash
dotnet add package Qodalis.Cli.Plugin.Database.Postgres
```

```csharp
using Qodalis.Cli.Plugin.Database.Postgres;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddPlugin<PostgresDatabasePluginEntry>(new { connectionString = "Host=localhost;Database=app;Username=app;Password=..." });
    cli.AddPlugin<Qodalis.Cli.Plugin.Audit.EfCore.AuditEfCorePluginEntry>(new { dialect = "postgres" });
});
```

The entry (plugin id `database-postgres`, order Driver (1), before every other plugin) is `CliDatabaseDriverPluginEntry` of
`Qodalis.Cli`: the drivers it registers are on `CliBuilder.DatabaseDrivers`. See
[Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard).

## Dependencies

- `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3
- `Npgsql` 10.0.3

## License

MIT
