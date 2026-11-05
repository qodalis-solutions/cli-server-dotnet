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

### Inside a job

`ICliJobExecutionContext` gives a run:

- `Logger`: the run's log lines (`Info`, `Warning`, ...), kept with the execution.
- `Properties`: the run's own values, saved with the execution and returned by the API, e.g. `context.Properties["items"] = 32`. Values must serialize to JSON.
- `Services`: a service scope created for this run and disposed when it ends, to resolve scoped services (a `DbContext`, a repository) from a job registered as an instance.
- `Discard()`: leaves a completed run out of the history, for a frequent job whose run found nothing to do. Failed runs are always kept.

```csharp
public class SyncOrdersJob : ICliJob
{
    public async Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken)
    {
        var orders = context.Services.GetRequiredService<IOrderSync>();
        var synced = await orders.SyncAsync(cancellationToken);
        if (synced == 0)
        {
            context.Discard();
            return;
        }
        context.Properties["synced"] = synced;
    }
}
```

To run a job from your own code (e.g. when something happened), resolve `ICliJobScheduler` and call `TriggerAsync(id)`; `Jobs` lists the registrations with their status.

## Configuration Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `Id` | `string?` | Name (`name-2`, ... for duplicates) | Stable id in the API, the history and the saved state |
| `Name` | `string?` | Class name | Job display name |
| `Description` | `string?` | Name | Human-readable description |
| `Group` | `string?` | `null` | Logical grouping |
| `Schedule` | `string?` | `null` | Cron expression (5-field) |
| `Interval` | `TimeSpan?` | `null` | Fixed interval between runs; across restarts the next run is the last run plus the interval |
| `RunOnStart` | `bool` | `false` | Run once right after the scheduler starts (active jobs only) |
| `Enabled` | `bool` | `true` | Whether the job starts active. `false` keeps it stopped whatever state was saved; otherwise a saved pause or stop (made from the API) is kept across restarts |
| `MaxRetries` | `int` | `1` | Retry count on failure |
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
| GET | `/executions` | Executions across jobs, newest first: `?job=a,b&group=&status=&since=&limit=&offset=` |

Jobs without a schedule or interval run only on request (`trigger`). Executions carry `properties`, the job's own
values. Keep job ids stable (`Id`): saved pauses and history are found again by id after a restart.

Each job carries `actions`: the actions it offers (`trigger`, `pause`, `resume`, `stop`, `cancel`, `update`), or
`null` for all of them. An action the job manager does not offer answers `501` with code `NOT_SUPPORTED`.

## MCP tools

When the server's MCP endpoint is enabled (`cli.AddMcp()`), the plugin contributes `jobs_list`, `jobs_get {id}`,
`jobs_history {id, limit?=20}` (`jobs:read`) and `jobs_trigger`, `jobs_pause`, `jobs_resume`, `jobs_stop`,
`jobs_cancel` `{id}` (`jobs:manage`; `id` is a job id or a unique job name), backed by the same job manager as the REST API.

## Custom Storage

By default, execution history is stored in memory. Use [Qodalis.Cli.Plugin.Jobs.EfCore](../jobs-efcore/README.md) for a
database, or provide your own `ICliJobStorageProvider`:

```csharp
cli.SetJobStorageProvider(new MyDatabaseStorageProvider());
```

Besides saving and reading executions and states, a store may implement `QueryExecutionsAsync` (executions across
jobs; the default merges per-job listings), `DeleteExecutionAsync` (for `Discard()`; the default keeps the run) and
`PruneAsync(before)` (delete old executions; the default keeps everything). Call `PruneAsync` on a schedule, for
example from a job, to keep the history bounded.

## Jobs that run elsewhere

The REST API, the MCP tools and the dashboard's jobs page work on an `ICliJobManager`. The default one runs the
jobs registered with `AddJob` in this process. When the host's jobs run somewhere else (a worker service, a queue,
its own scheduler), implement `ICliJobManager` over them and register it; the CLI and the dashboard then list and
control those jobs:

```csharp
cli.SetJobManager<MyJobManager>();   // scoped: it may use the request's services
```

Implement `GetJobsAsync`, `TriggerAsync`, `PauseAsync`, `ResumeAsync`, `GetExecutionsAsync` and
`GetExecutionAsync` (`QueryExecutionsAsync` defaults to merging `GetExecutionsAsync` per job). `StopAsync`, `CancelAsync` and `UpdateAsync` default to `NotSupportedException` (501); set
`CliJobInfo.Actions` so clients hide what a job does not offer. Throw `KeyNotFoundException` for an unknown job
(404) and `InvalidOperationException` when the job's state refuses the action (409).

## License

MIT
