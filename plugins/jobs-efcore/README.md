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

Three tables are created automatically on first use:

| Table | Purpose |
|-------|---------|
| `job_executions` | Execution records (status, timestamps, duration, error, retry attempt) |
| `job_log_entries` | Structured log entries per execution (timestamp, level, message) |
| `job_states` | Persistent job state (active/paused/stopped, last run time) |

## License

MIT
