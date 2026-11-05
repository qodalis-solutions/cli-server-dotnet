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

Each job carries `actions`: the actions it offers (`trigger`, `pause`, `resume`, `stop`, `cancel`, `update`), or
`null` for all of them. An action the job manager does not offer answers `501` with code `NOT_SUPPORTED`.

## MCP tools

When the server's MCP endpoint is enabled (`cli.AddMcp()`), the plugin contributes `jobs_list`, `jobs_get {id}`,
`jobs_history {id, limit?=20}` (`jobs:read`) and `jobs_trigger`, `jobs_pause`, `jobs_resume`, `jobs_stop`,
`jobs_cancel` `{id}` (`jobs:manage`; `id` is a job id or a unique job name), backed by the same job manager as the REST API.

## Custom Storage

By default, execution history is stored in memory. Provide a custom `ICliJobStorageProvider` for persistence:

```csharp
cli.SetJobStorageProvider(new MyDatabaseStorageProvider());
```

## Jobs that run elsewhere

The REST API, the MCP tools and the dashboard's jobs page work on an `ICliJobManager`. The default one runs the
jobs registered with `AddJob` in this process. When the host's jobs run somewhere else (a worker service, a queue,
its own scheduler), implement `ICliJobManager` over them and register it; the CLI and the dashboard then list and
control those jobs:

```csharp
cli.SetJobManager<MyJobManager>();   // scoped: it may use the request's services
```

Implement `GetJobsAsync`, `TriggerAsync`, `PauseAsync`, `ResumeAsync`, `GetExecutionsAsync` and
`GetExecutionAsync`. `StopAsync`, `CancelAsync` and `UpdateAsync` default to `NotSupportedException` (501); set
`CliJobInfo.Actions` so clients hide what a job does not offer. Throw `KeyNotFoundException` for an unknown job
(404) and `InvalidOperationException` when the job's state refuses the action (409).

## License

MIT
