# Qodalis.Cli.Plugin.Migrations

Database migrations for the Qodalis CLI server: versioned schema changes of PostgreSQL, MySQL/MariaDB, SQL Server
and SQLite databases, applied under a lock, recorded with who ran them, when and how long they took, and shown, run
and rolled back from the admin dashboard, the web terminal (`migrations`), MCP and the API. The same plugin exists on
the Node and Python servers, with the same API, tables and commands.

```bash
dotnet add package Qodalis.Cli.Plugin.Migrations
```

## Use

```csharp
builder.Services.AddControllers().AddCli(cli => cli
    .AddMigrations(m => m
        .AddDatabase("main", db => db
            .UseConnection(_ => new NpgsqlConnection(connectionString))
            .ApplyMigrationsOnStartup())
        .AddSqlFolder("main", "./migrations")));
```

`UseConnection` returns a new, closed ADO.NET connection: Npgsql, MySqlConnector (or MySql.Data), Microsoft.Data.SqlClient
or Microsoft.Data.Sqlite. The plugin needs none of the drivers itself. `ApplyMigrationsOnStartup()` applies what is
pending before the server takes requests; a failure stops startup with the error.

A host on EF Core uses [`Qodalis.Cli.Plugin.Migrations.EfCore`](../migrations-efcore): its `DbContext`'s migrations
in the same plugin, in place of `Database.MigrateAsync()`.

## Migrations

- **A folder of SQL files**: `0001_create_users.up.sql`, with an optional `0001_create_users.down.sql` to roll it back.
  A file starting with `-- qcli:no-transaction` runs outside a transaction (e.g. `CREATE INDEX CONCURRENTLY`). On SQL
  Server, `GO` lines separate batches.
- **Code**: `m.AddMigrations("main", "billing", [new CliMigration { Id = "1", Name = "create invoices", Up = "CREATE TABLE …", Down = CliMigrationStep.FromSql("DROP TABLE …") }])`.
  A step is SQL, SQL per dialect (`CliMigrationStep.FromSql(new Dictionary<string, string> { ["postgres"] = "…", ["sqlite"] = "…" })`)
  or code given the session (`CliMigrationStep.FromCode(db => db.ExecuteAsync("…", arg))`). This is how plugins ship
  their schema.

A database can have several sources; they apply in the order they were added, and `AddMigrations` can be called again
(plugins add their sources this way). Released migrations are never edited or reordered: add a new one instead. Each
migration runs in its own transaction together with its history row (MySQL commits DDL statements on its own, so a
failing MySQL migration may leave a table behind).

## Status, runs and safety

- `QcliMigrations` holds the applied migrations (source, id, name, checksum, applied at/by, duration) and
  `QcliMigrationRuns` every run (direction, target, trigger, who, start/end, status, error, what it applied). The
  names follow the plugin table names (`TablePrefix`, `Schema`, `Tables` on the database's options).
- A migration is `applied`, `pending`, `changed` (its SQL differs from what was applied), `missing` (applied, no longer
  in its source) or `failed` (the last run stopped at it). Runs are refused while one is `changed` or `missing`, unless
  `force`.
- One run at a time: a lock per database (`pg_advisory_lock`, `GET_LOCK`, `sp_getapplock`) across processes, so two
  services starting together apply once. The API and terminal answer busy (409); startup waits.
- Every run publishes `migrations:run` (audience `migrations:read`) and, when an audit store is registered (users or
  audit plugin), writes `migrations.up` / `migrations.down` to the audit log.

## API, terminal and MCP

| | |
|---|---|
| `GET /api/v1/qcli/migrations` | databases: applied, pending, problems, last run |
| `GET /api/v1/qcli/migrations/{db}` | its migrations and their status |
| `GET /api/v1/qcli/migrations/{db}/runs?page=&size=` | run history, newest first |
| `GET /api/v1/qcli/migrations/{db}/sql?direction=up\|down&to=` | the SQL a run would execute (dry run) |
| `POST /api/v1/qcli/migrations/{db}/up` `{ to?, force? }` | apply pending migrations |
| `POST /api/v1/qcli/migrations/{db}/down` `{ to }` | roll back to `to` (kept; `base` for everything) |

Permissions on a protected server: `migrations:read` for reading, `migrations:run` for up and down (both in the
permission catalog). Terminal: `migrations`, `migrations status <db>`, `migrations up <db> [--to <id>] [--dry-run] [--force]`,
`migrations down <db> --to <id> [--dry-run]`. With `cli.AddMcp()`: `migrations_status`, `migrations_runs`,
`migrations_sql`, `migrations_up`, `migrations_down`.

## Helpers for store authors

`CliSqlSchema`: `TableExistsAsync`, `ColumnExistsAsync`, `RenameTableAsync`, `RenameColumnAsync` and
`AdoptLegacyTableAsync(db, legacy, table, columns)` (renames a previous release's table and its columns to the new
names, keeping the data) work on every dialect. `CliMigrationRunner` runs a store's own source without the plugin
(`new CliMigrationDatabaseOptions { Connection = … }.WithSource(source)`), as filesystem-sqlite does.

## Tests

`MigrationsRunnerTests` run on SQLite, and on PostgreSQL, MySQL and SQL Server when `QCLI_TEST_POSTGRES_CS`,
`QCLI_TEST_MYSQL_CS` and `QCLI_TEST_MSSQL_CS` hold ADO.NET connection strings to databases the tests may write to.
