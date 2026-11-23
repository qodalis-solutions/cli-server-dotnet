# Qodalis.Cli.Plugin.Databases

The Qodalis CLI server's relational databases (PostgreSQL, MySQL/MariaDB, SQL Server, SQLite): their size,
connections, load and largest tables, and their migrations, which are versioned schema changes applied under a lock,
recorded with who ran them, when and how long they took, and shown, run and rolled back from the admin dashboard, the
web terminal (`databases`), MCP and the API. The same plugin exists on the Node and Python servers, with the same API,
tables and commands.

```bash
dotnet add package Qodalis.Cli.Plugin.Databases
```

## Use

```csharp
builder.Services.AddControllers().AddCli(cli => cli
    .AddDatabases(d => d
        .AddDatabase("main", db => db
            .UseConnection(_ => new NpgsqlConnection(connectionString))
            .ApplyMigrationsOnStartup())
        .AddSqlFolder("main", "./migrations")));
```

`UseConnection` returns a new, closed ADO.NET connection: Npgsql, MySqlConnector (or MySql.Data), Microsoft.Data.SqlClient
or Microsoft.Data.Sqlite. The plugin needs none of the drivers itself. A database without migrations is shown with its
stats only. `ApplyMigrationsOnStartup()` applies what is pending before the server takes requests; a failure stops
startup with the error.

A host on EF Core uses [`Qodalis.Cli.Plugin.Databases.EfCore`](../databases-efcore): its `DbContext`'s migrations
in the same plugin, in place of `Database.MigrateAsync()`.

## Stats

`GET /api/v1/qcli/databases/{db}/stats` (and `databases stats <db>`, MCP `databases_stats`) reads:

| | PostgreSQL | MySQL / MariaDB | SQL Server | SQLite |
|---|---|---|---|---|
| server | version, start, uptime | version, uptime | version, edition, start (VIEW SERVER STATE) | SQLite version |
| size | database, indexes | data, indexes | data, log, indexes | pages × page size |
| connections | active, open (this database), max | running, connected, max (server) | active, open (this database), max | |
| counters | transactions, commits, rollbacks, rows read/written (this database) | queries, transactions, rows read/written (server) | batch requests, transactions (VIEW SERVER STATE) | |
| cache hit ratio | blocks hit / read | buffer pool | buffer cache | |
| largest tables | 10 by size, live rows | 10 by size, estimated rows | 10 by reserved size, rows | 10 by size (dbstat) else rows |

Counters run since the server started; the dashboard samples them while the page is open and charts their rates.
Each group is read on its own: one the database user may not read (e.g. SQL Server without `VIEW SERVER STATE`) is
left out and listed in `unavailable` with the error. The list (`GET /api/v1/qcli/databases`) carries each database's
size as `sizeBytes`.

## Migrations

- **A folder of SQL files**: `0001_create_users.up.sql`, with an optional `0001_create_users.down.sql` to roll it back.
  A file starting with `-- qcli:no-transaction` runs outside a transaction (e.g. `CREATE INDEX CONCURRENTLY`). On SQL
  Server, `GO` lines separate batches.
- **Code**: `d.AddMigrations("main", "billing", [new CliMigration { Id = "1", Name = "create invoices", Up = "CREATE TABLE …", Down = CliMigrationStep.FromSql("DROP TABLE …") }])`.
  A step is SQL, SQL per dialect (`CliMigrationStep.FromSql(new Dictionary<string, string> { ["postgres"] = "…", ["sqlite"] = "…" })`)
  or code given the session (`CliMigrationStep.FromCode(db => db.ExecuteAsync("…", arg))`). This is how plugins ship
  their schema.

A database can have several sources; they apply in the order they were added, and `AddDatabases` can be called again
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
- Every run publishes `databases:migration-run` (audience `databases:read`) and, when an audit store is registered
  (users or audit plugin), writes `migrations.up` / `migrations.down` to the audit log.

## API, terminal and MCP

| | |
|---|---|
| `GET /api/v1/qcli/databases` | databases: size, applied, pending, problems, last run |
| `GET /api/v1/qcli/databases/{db}` | one database, as in the list |
| `GET /api/v1/qcli/databases/{db}/stats` | size, connections, load counters, cache hit ratio, largest tables |
| `GET /api/v1/qcli/databases/{db}/migrations` | its migrations and their status |
| `GET /api/v1/qcli/databases/{db}/migrations/runs?page=&size=` | run history, newest first |
| `GET /api/v1/qcli/databases/{db}/migrations/sql?direction=up\|down&to=` | the SQL a run would execute (dry run) |
| `POST /api/v1/qcli/databases/{db}/migrations/up` `{ to?, force? }` | apply pending migrations |
| `POST /api/v1/qcli/databases/{db}/migrations/down` `{ to }` | roll back to `to` (kept; `base` for everything) |

Permissions on a protected server: `databases:read` for reading, `databases:migrate` for up and down (both in the
permission catalog). Terminal (`databases`, alias `db`): `databases list`, `databases stats <db>`,
`databases migrations <db>`, `databases up <db> [--to <id>] [--dry-run] [--force]`,
`databases down <db> --to <id> [--dry-run]`. With `cli.AddMcp()`: `databases_list`, `databases_stats`,
`migrations_status`, `migrations_runs`, `migrations_sql`, `migrations_up`, `migrations_down`.

## Dashboard page

The admin dashboard shows a Databases page (group Data, `databases:read`): the databases with their size, and for each
one its stats (size, connections, load over time, largest tables), its migrations by status and the runs, and, with
`databases:migrate`, Apply pending / Migrate to here / Roll back to here with the SQL each run executes. It is
`@qodalis/cli-dashboard-module-databases`, embedded in the package at build (from the sibling `cli-server-dashboard`
repo, else the npm package), served at `/qcli/databases` and registered as a dashboard extension by `AddDatabases`,
with its cards on the dashboard's Overview (`Widgets = ["overview"]`); nothing else to call.
`cli.ConfigureDashboardModule("databases", files)` serves another build, `null` leaves the page out.

## Helpers for store authors

`CliSqlSchema`: `TableExistsAsync`, `ColumnExistsAsync`, `RenameTableAsync`, `RenameColumnAsync` and
`AdoptLegacyTableAsync(db, legacy, table, columns)` (renames a previous release's table and its columns to the new
names, keeping the data) work on every dialect. `CliMigrationRunner` runs a store's own source without the plugin
(`new CliMigrationDatabaseOptions { Connection = … }.WithSource(source)`), as filesystem-sqlite does.

## Tests

`MigrationsRunnerTests` (runner and stats) run on SQLite, and on PostgreSQL, MySQL and SQL Server when
`QCLI_TEST_POSTGRES_CS`, `QCLI_TEST_MYSQL_CS` and `QCLI_TEST_MSSQL_CS` hold ADO.NET connection strings to databases
the tests may write to.
