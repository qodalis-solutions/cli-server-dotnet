# Qodalis.Cli.Plugin.Jobs.EfCore

Entity Framework Core persistence provider for [Qodalis CLI Server Jobs](../jobs/README.md). Persists job execution history, logs, and state to any EF Core-supported database.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.Jobs.EfCore
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.Jobs.EfCore;

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        cli.AddJob<HealthCheckJob>(o =>
        {
            o.Name = "health-check";
            o.Interval = TimeSpan.FromSeconds(30);
        });

        // Persist job history to SQLite
        cli.AddEfCoreJobStorage(o => o.UseSqlite("Data Source=jobs.db"));
    });
```

Any EF Core database provider works — SQLite, SQL Server, PostgreSQL, MySQL, etc. Just configure the `DbContextOptionsBuilder` accordingly:

```csharp
// PostgreSQL
cli.AddEfCoreJobStorage(o => o.UseNpgsql(connectionString));

// SQL Server
cli.AddEfCoreJobStorage(o => o.UseSqlServer(connectionString));
```

## Database Schema

Three tables are created automatically on first use, also inside a database that already has other tables (your
application's own):

| Table | Purpose |
|-------|---------|
| `QcliJobExecutions` | Execution records (status, timestamps, duration, error, retry attempt) |
| `QcliJobLogEntries` | Structured log entries per execution (timestamp, level, message) |
| `QcliJobStates` | Persistent job state (active/paused/stopped, last run time) |

Executions keep their `properties` (the job's own values) as JSON.

## Table names (1.0.0-beta.30)

The tables are `QcliJobExecutions`, `QcliJobLogEntries` and `QcliJobStates` (cli-server-dotnet#99: `Qcli` + PascalCase, the same on the three servers). On the first start
after an upgrade the store renames the `job_executions`, `job_log_entries` and `job_states` of 1.0.0-beta.29 and older to the new names, with their keys and indexes, keeping the rows.
The store's options (`AddEfCoreJobStorage(..., o => ...)`) set the names: `TablePrefix` (default `Qcli`), `Tables["…"]` for one table, `Schema`, and
`LegacyTableNames = true` to keep the old names.

A host that maps the tables in its own model (`modelBuilder.ApplyJobStorageModel()`) gets the new names there too: its next
`dotnet ef migrations add` generates the `RenameTable`s, or it keeps the old names with `ApplyJobStorageModel("job_executions", "job_log_entries", "job_states")` and `LegacyTableNames = true`.

With the [databases plugin](../databases-efcore), the schema is a source of a database, so the dashboard shows it and startup applies it.

```csharp
cli.AddDatabases(d => d.AddJobStorageSchema("cli", db => db.ApplyMigrationsOnStartup()));
```

## Host migrations

An application that manages its database with EF Core migrations can create the tables in its own migrations
instead (and fill them from its own tables there):

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ApplyJobStorageModel();
}
```

## Retention

The history is kept until pruned. Set the jobs plugin's [history retention](../jobs/README.md#history-retention)
(`cli.ConfigureJobs(o => o.History.MaxAge = TimeSpan.FromDays(30))` or `Jobs:History` in configuration); this store
prunes with set-based deletes (the runs and their log entries), without loading the runs.

## License

MIT
