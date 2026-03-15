# Qodalis.Cli.Plugin.Jobs

Background job scheduling plugin for [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet). Provides cron and interval-based job scheduling, execution history, retry policies, and a REST API for management.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.Jobs
```

## Quick Start

1. Implement `ICliJob`:

```csharp
using Qodalis.Cli.Abstractions.Jobs;

public class HealthCheckJob : ICliJob
{
    public async Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken)
    {
        context.Logger.Info("Running health check...");
        // your logic here
        context.Logger.Info("Health check passed");
    }
}
```

2. Register the job via `CliBuilder`:

```csharp
builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        cli.AddJob<HealthCheckJob>(o =>
        {
            o.Name = "health-check";
            o.Description = "Periodic health check";
            o.Interval = TimeSpan.FromSeconds(30);
        });

        // Or register an instance directly:
        cli.AddJob(new HealthCheckJob(), o =>
        {
            o.Name = "health-check";
            o.Schedule = "*/5 * * * *"; // cron expression
        });
    });
```

The plugin automatically registers the `CliJobScheduler` as a hosted service and mounts the REST controller at `/api/v1/qcli/jobs`.

## Configuration Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `Name` | `string?` | Class name | Job display name |
| `Description` | `string?` | Name | Human-readable description |
| `Group` | `string?` | `null` | Logical grouping |
| `Schedule` | `string?` | `null` | Cron expression (5-field) |
| `Interval` | `TimeSpan?` | `null` | Fixed interval between runs |
| `Enabled` | `bool` | `true` | Whether the job starts active |
| `MaxRetries` | `int` | `0` | Retry count on failure |
| `Timeout` | `TimeSpan?` | `null` | Max execution duration |
| `OverlapPolicy` | `JobOverlapPolicy` | `Skip` | `Skip`, `Queue`, or `Cancel` |

## REST API

All endpoints are mounted at `/api/v1/qcli/jobs`.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/` | List all jobs |
| GET | `/{id}` | Get job details |
| POST | `/{id}/trigger` | Trigger immediate execution |
| POST | `/{id}/pause` | Pause scheduled execution |
| POST | `/{id}/resume` | Resume a paused job |
| POST | `/{id}/stop` | Stop job and cancel if running |
| POST | `/{id}/cancel` | Cancel current execution only |
| PUT | `/{id}` | Update job options |
| GET | `/{id}/history` | Paginated execution history |
| GET | `/{id}/history/{execId}` | Execution detail with logs |

## Custom Storage

By default, execution history is stored in memory. Provide a custom `ICliJobStorageProvider` for persistence:

```csharp
cli.SetJobStorageProvider(new MyDatabaseStorageProvider());
```

## License

MIT
