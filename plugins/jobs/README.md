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
| `Tags` | `IReadOnlyList<string>` | `[]` | Free-form labels to filter jobs by (`country:ro`, `bank`): each 1-64 characters without commas, at most 20, trimmed, duplicates (ignoring case) dropped (`CliJobTags`). Job info always carries `tags`; a custom `ICliJobManager` fills `CliJobInfo.Tags` |
| `Schedule` | `string?` | `null` | Cron expression (5-field) |
| `Interval` | `TimeSpan?` | `null` | Fixed interval between runs; across restarts the next run is the last run plus the interval |
| `RunOnStart` | `bool` | `false` | Run when the scheduler starts unless the job ran recently (active jobs only; see [Run on start](#run-on-start)) |
| `Enabled` | `bool` | `true` | Whether the job starts active. `false` keeps it stopped whatever state was saved; otherwise a saved pause or stop (made from the API) is kept across restarts |
| `MaxRetries` | `int` | `1` | Retry count on failure |
| `Timeout` | `TimeSpan?` | `null` | Max execution duration |
| `OverlapPolicy` | `JobOverlapPolicy` | `Skip` | What a due run does when `MaxConcurrentRuns` runs are going: `Skip`, `Queue`, or `Cancel` |
| `MaxConcurrentRuns` | `int` | `1` | How many runs of the job may go at the same time (at least 1) |

## Concurrent runs

A job runs at most `MaxConcurrentRuns` times at once. A run holds its slot from its start until it ends, **including
its retries and the delays before them**, so a timer tick or a trigger during a retry delay does not start a second
run. Taking a slot is atomic: ticks and triggers at the same moment never exceed the limit.

When a run is due (timer, trigger, run on start) and every slot is taken, `OverlapPolicy` decides:

- `Skip`: the due run is dropped; a trigger is refused (`409 JOB_ALREADY_RUNNING`).
- `Queue`: the run waits and starts when a slot frees. At most one run waits per job: several due runs while full make
  one more run, not a backlog. A trigger is accepted: `200 { "message": "Job queued", "queued": true }` (a run that
  started answers `{ "message": "Job triggered", "queued": false }`; MCP `jobs_trigger` says "Job queued").
- `Cancel`: the oldest running run is cancelled; the new run waits (`queued`, and a trigger answers `"Job queued"`)
  and starts once the cancelled run ended, so the limit holds. A further due run meanwhile joins the waiting one
  instead of cancelling another run. Above a lowered limit nothing is cancelled: the due run is skipped (a trigger gets
  `409 JOB_ALREADY_RUNNING`).

Lowering `MaxConcurrentRuns` cancels nothing: runs finish, and new runs start once fewer are going. Raising it starts a
queued run. `cancel` cancels every running run of the job and drops a queued one; `stop` does the same and disables
the schedule. A run cancelled that way ends `cancelled` (`job:cancelled`), also when the job has a `Timeout`; only a
real timeout ends `timedout` (`job:timed_out`).

Job info (`GET /jobs`, `GET /jobs/{id}`, `jobs_list`, `jobs_get`, `CliJobInfo`) carries `tags`, `maxConcurrentRuns`,
`runningExecutionIds` (the attempts executing now, oldest first; empty when idle; a run waiting for its retry keeps
its slot but has no entry), `currentExecutionId` (the newest executing attempt, or null) and `queued` (a run waits for
a slot).

## Run on start

`RunOnStart` runs a job when the server starts unless it ran recently, so a restart, a crash loop or a double deploy
does not run every job again:

- Interval job: only when it never ran or its next run is due (last run plus the interval has passed); otherwise the
  timer keeps the rhythm from the last run.
- Cron job: only when it never ran or missed an occurrence (one between the last run and now).
- A job without a schedule (run on request only): on every start.

The run goes through the same slots as any due run. Cron timers compute the next occurrence from the one that just
fired, so a tick that fires a few milliseconds early never runs an occurrence twice.

## History retention

Every run is kept until something deletes it. Set limits and the plugin prunes the history itself (off by default):

```csharp
cli.ConfigureJobs(o =>
{
    o.History.MaxAge = TimeSpan.FromDays(30);         // delete runs older than this
    o.History.MaxRunsPerJob = 500;                    // keep at most this many runs per job (newest kept)
    o.History.FailedMaxAge = TimeSpan.FromDays(90);   // optional: failed runs kept longer
    o.History.FailedMaxRunsPerJob = 1000;             // optional
    o.History.PruneInterval = TimeSpan.FromHours(6);  // how often to prune (default 1 day)
});
```

Or from configuration (code wins; `ConfigureJobs` works before or after `AddJob`):

```json
{ "Jobs": { "History": { "MaxAge": "30.00:00:00", "MaxRunsPerJob": 500, "FailedMaxAge": "90.00:00:00", "PruneInterval": "06:00:00" } } }
```

- A run is deleted when it breaks any limit that is set. Failed runs (status `failed` only; timed-out and cancelled
  runs follow the normal limits) use `FailedMaxAge` when it is set, else `MaxAge`.
- Count limits rank runs newest first; a run ranked at or past its limit goes. With `FailedMaxRunsPerJob` set, failed
  runs are counted on their own (the newest N failed runs stay) and the other runs on their own against
  `MaxRunsPerJob`; without it, every run of the job counts against `MaxRunsPerJob`.
- Running executions and the newest run of each job are never deleted, but take their place in the count.
- Runs of job ids that are no longer registered (a removed job) are pruned with the global limits, when any are set
  and the store lists its job ids (`GetExecutionJobIdsAsync`; the in-memory and EF Core stores do).
- Count limits must be integers of at least 1 and durations positive: an invalid value throws when the job is
  registered or the scheduler is built.
- Per job, `History` on the job's options replaces the global field it sets, and `KeepAll` keeps the job's whole
  history:

  ```csharp
  cli.AddJob<FetchRatesJob>(o => { o.Interval = TimeSpan.FromMinutes(30); o.History.MaxRunsPerJob = 50; });
  cli.AddJob<MonthlyReportJob>(o => { o.Schedule = "0 0 1 * *"; o.History.KeepAll = true; });
  ```

With a limit set (globally or on a job), the system job `jobs:prune-history` (group `system`, tag `system`) prunes
every `PruneInterval`, and on start when it is due. Each run sets the property `deleted` to how many executions it
deleted and logs it; pause or trigger it like any job. Its own history follows the global limits.

Job info (`GET /jobs`, `GET /jobs/{id}`, `jobs_list`, `jobs_get`) carries `historyRetention`, the limits in effect
for the job (durations in milliseconds), or `null` when none apply:

```json
"historyRetention": { "maxAge": 2592000000, "maxRunsPerJob": 50, "failedMaxAge": null, "failedMaxRunsPerJob": null, "overridden": true, "keepAll": false }
```

A `keepAll` job reports every limit as `null` with `overridden` and `keepAll` true. Pruning runs in the process that
hosts the scheduler, whatever job manager the API uses. A custom `ICliJobManager` reports the same field with
`job.History.Resolve(global)` (`CliJobHistoryOptions.Resolve` in the abstractions), where `global` is the
`JobHistoryRetention` its scheduler applies (`ICliJobScheduler.GetHistoryRetention(registration)` in a process that
hosts this plugin's scheduler).

The `jobs` [config provider](../../README.md#config-providers) (section `history`) changes the global limits at runtime
(`maxAge`, `maxRunsPerJob`, `failedMaxAge`, `failedMaxRunsPerJob`; `pruneInterval` is read-only): the next
`jobs:prune-history` run applies them, and the job is registered when a first limit is set
(`CliJobScheduler.SetHistoryDefaults`). Its changes are kept in the server's default config store unless the plugin
gets its own, `cli.ConfigureJobs(o => o.ConfigStore = store)`; the host's store for the id
(`cli.SetConfigStore(store, "jobs")`, e.g. `cli.AddEfCoreConfigStore(..., providerId: "jobs")`) still wins.

## Interrupted runs

When the scheduler starts, the runs of its registered jobs that its store still has as `running` (left by a process
that stopped during them) are finished as `cancelled` with the error `Interrupted: the server stopped during the run`,
completed at their last log entry when that is later than their start, else at their start. Only at start and only in the scheduler's own store; no events are published for them.
Several server processes sharing one store therefore finish each other's runs when one of them starts: run a job's
schedule in one process only.

## REST API

All endpoints are mounted at `/api/v1/qcli/jobs`.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/` | List the jobs; `?tag=a&tag=b` (repeatable or comma-separated) lists only those that have every tag, ignoring case |
| GET | `/{id}` | Get job details |
| POST | `/{id}/trigger` | Trigger immediate execution: `{ message, queued }` (`queued: true` when the run waits for a slot) |
| POST | `/{id}/pause` | Pause scheduled execution |
| POST | `/{id}/resume` | Resume a paused job |
| POST | `/{id}/stop` | Stop job: cancel every running run, drop a queued one, disable the schedule |
| POST | `/{id}/cancel` | Cancel every running run and drop a queued one (`409 JOB_NOT_RUNNING` when idle) |
| PUT | `/{id}` | Update job options: `description`, `group`, `tags` (replaces them, `null` clears, invalid tags are `400 INVALID_VALUE`), `schedule`, `interval`, `maxRetries`, `retryDelay`, `retryStrategy`, `timeout`, `overlapPolicy`, `maxConcurrentRuns` (an integer >= 1: below 1 is `400 INVALID_VALUE`, a non-integer is `400` too) |
| GET | `/{id}/history` | Paginated execution history |
| GET | `/{id}/history/{execId}` | Execution detail with logs |
| DELETE | `/{id}/history/{execId}` | Delete a finished run and its logs (`404 EXECUTION_NOT_FOUND`, `409 EXECUTION_RUNNING`) |
| DELETE | `/{id}/history` | Delete the job's finished runs, or only those of `?status=` (e.g. `failed`); answers `{ deleted }` |
| GET | `/executions` | Executions across jobs, newest first: `?job=a,b&group=&tag=&status=&since=&limit=&offset=` (`tag` as in `/`) |
| GET | `/stats` | Statistics of the runs started in `[since, until)` (default the last 24 hours): outcomes per column (`buckets`, default 24, 1..200), totals and per-job figures (success rate, mean/p95/max duration, last failure, the 12 newest runs as `{ id, status, startedAt, duration }`, `id` being the execution id); `job`, `group` and `tag` as in `/executions` |

Jobs without a schedule or interval run only on request (`trigger`). Executions carry `properties`, the job's own
values. Keep job ids stable (`Id`): saved pauses and history are found again by id after a restart.

Each job carries `actions`: the actions it offers (`trigger`, `pause`, `resume`, `stop`, `cancel`, `update`,
`delete-runs`), or
`null` for all of them. An action the job manager does not offer answers `501` with code `NOT_SUPPORTED`.

## MCP tools

When the server's MCP endpoint is enabled (`cli.AddMcp()`), the plugin contributes `jobs_list` (`tags`: only jobs with every tag), `jobs_get {id}`,
`jobs_history {id, limit?=20}` (`jobs:read`) and `jobs_trigger`, `jobs_pause`, `jobs_resume`, `jobs_stop`,
`jobs_cancel` `{id}` and `jobs_delete_runs` `{id, executionId?, status?}` (`jobs:manage`; `id` is a job id or a unique
job name), backed by the same job manager as the REST API.

A custom job manager deletes runs by implementing `DeleteExecutionAsync`; `DeleteExecutionsAsync` then pages through
the history and deletes each finished run (override it to delete in one query). Without it the delete endpoints answer
`501`. The default scheduler deletes through `ICliJobStorageProvider.DeleteExecutionAsync` (in-memory and EF Core).

## Events

Job changes are published on the server's event bus (`ICliEventBus`) and sent on the events WebSocket to sockets with
`jobs:read`, as flat messages (`{ "type": "job:completed", "timestamp": "…", "jobId": "…", "executionId": "…", "duration": 1520 }`):

| Type | Fields | Published by |
|---|---|---|
| `job:started`, `job:cancelled`, `job:timed_out` | jobId, executionId | scheduler |
| `job:completed` | jobId, executionId, duration | scheduler, after the run is saved |
| `job:failed` | jobId, executionId, error | scheduler |
| `job:retrying` | jobId, executionId, attempt, maxRetries, delayMs | scheduler |
| `job:paused`, `job:resumed`, `job:stopped` | jobId, status (`paused`, `active`, `stopped`) | scheduler |
| `job:triggered`, `job:updated` | jobId, actor | REST API and MCP tools |
| `job:runs-deleted` | jobId, executionId (one run) or count (bulk, when runs were deleted), actor | REST API and MCP tools |

`job:triggered`, `job:updated` and `job:runs-deleted` are published by the API layer, so a custom job manager (see
below) gets them without publishing anything. A manager whose jobs run elsewhere can publish its own run events through
`ICliEventBus` with `CliEventAudience.WithPermission("jobs:read")`.

## Custom Storage

By default, execution history is stored in memory. Use [Qodalis.Cli.Plugin.Jobs.EfCore](../jobs-efcore/README.md) for a
database, or provide your own `ICliJobStorageProvider`:

```csharp
cli.SetJobStorageProvider(new MyDatabaseStorageProvider());
```

Besides saving and reading executions and states, a store may implement `QueryExecutionsAsync` (executions across
jobs; the default merges per-job listings), `DeleteExecutionAsync` (for `Discard()`; the default keeps the run) and
`PruneAsync(before)` (delete old executions; the default keeps everything), and
`PruneAsync(jobId, retention, now)`, which the [history retention](#history-retention) calls per job (the default
reads the job's runs and deletes the expired ones with `DeleteExecutionAsync`; override it with a set-based delete),
and `GetExecutionJobIdsAsync` (the job ids that have runs, so removed jobs are pruned too; the default lists none), and
`SummarizeExecutionsAsync(since, jobIds)` (per job and status, the number of runs and the start of the newest one;
the default pages through `QueryExecutionsAsync` and groups in memory). `GET /jobs/stats` reads every run of its range through
`QueryExecutionsAsync`, a page at a time, so it works with any store or job manager.

Lists may leave the log entries out: `JobExecutionQuery.IncludeLogs` (default `false`) asks for them, and a store
that leaves them out sets `JobExecution.LogCount` (the API's `logCount` reads `LogEntryCount`, either one).
`GetExecutionAsync` returns one run with its logs; the run detail endpoint, `server jobs logs` and the dashboard read
logs through it. The in-memory store keeps the logs in its lists.

## Jobs that run elsewhere

The REST API, the MCP tools and the dashboard's jobs page work on an `ICliJobManager`. The default one runs the
jobs registered with `AddJob` in this process. When the host's jobs run somewhere else (a worker service, a queue,
its own scheduler), implement `ICliJobManager` over them and register it; the CLI and the dashboard then list and
control those jobs:

```csharp
cli.SetJobManager<MyJobManager>();   // scoped: it may use the request's services
```

Implement `GetJobsAsync`, `TriggerAsync`, `PauseAsync`, `ResumeAsync`, `GetExecutionsAsync` and
`GetExecutionAsync` (`QueryExecutionsAsync` defaults to merging `GetExecutionsAsync` per job). Map
`MaxConcurrentRuns`, `RunningExecutionIds` and `Queued` on `CliJobInfo` when your jobs have them, and implement
`TriggerWithOutcomeAsync` to report a queued trigger (the default calls `TriggerAsync` and reports a started run); the defaults (1, empty,
false) suit a manager without the option, and with `RunningExecutionIds` empty the API reports `CurrentExecutionId`
as the one running run. `StopAsync`, `CancelAsync` and `UpdateAsync` default to `NotSupportedException` (501); set
`CliJobInfo.Actions` so clients hide what a job does not offer. Throw `KeyNotFoundException` for an unknown job
(404) and `InvalidOperationException` when the job's state refuses the action (409).

## Dashboard page

The admin dashboard's Jobs page (`@qodalis/cli-dashboard-module-jobs`, `jobs:read`) comes with this package, with its cards on the dashboard's Overview (active jobs, runs of the last 24 hours, failing jobs), the command palette's entries (jump to a job) and its job search. It is embedded in the package at build (from the sibling cli-server-dashboard repo, else the npm package) and served at `/qcli/modules/jobs` outside the API; `cli.ConfigureDashboardModule("jobs", files)` serves another build, `null` leaves it out (see the root README's [Dashboard modules](../../README.md#dashboard-modules)).

## Terminal commands

The plugin has no server-side command: the web CLI plugin [`@qodalis/cli-server-jobs`](https://www.npmjs.com/package/@qodalis/cli-server-jobs)
drives its REST API as `server jobs`. Permissions on a protected server: `jobs:read` for reading, `jobs:manage` for
changes.

| Command | Permission | Does |
|---|---|---|
| `server jobs list` (`ls`) | `jobs:read` | Jobs of every connected server (`--group`, `--tag`) |
| `server jobs info <name\|id>` | `jobs:read` | Schedule, retries, timeout, overlap policy, current and last run |
| `server jobs history <name\|id>` | `jobs:read` | Past runs: status, start, duration, attempt, error |
| `server jobs runs [--job <id>] [--status failed] [-n 20]` | `jobs:read` | The latest runs of every job, newest first (`GET /executions`) |
| `server jobs stats` | `jobs:read` | Runs of the last 24 hours: outcomes, success rate, average and p95 duration, per job (`GET /stats`) |
| `server jobs logs <name\|id>` | `jobs:read` | Log entries of the latest run, or of one run |
| `server jobs watch` | `jobs:read` | Job events live (Ctrl+C stops) |
| `server jobs trigger <name\|id>` | `jobs:manage` | Runs a job now |
| `server jobs pause <name\|id>`, `server jobs resume <name\|id>` | `jobs:manage` | Takes a job off its schedule, puts it back |
| `server jobs stop <name\|id>`, `server jobs cancel <name\|id>` | `jobs:manage` | Stops the job (schedule off, run cancelled), or cancels only the run in progress |
| `server jobs edit <name\|id>` | `jobs:manage` | Edits description, group, tags, schedule, retries, timeout, overlap policy |
| `server jobs history clear <name\|id> [--status failed] [--yes]` | `jobs:manage` | Deletes the finished runs of a job (`DELETE /{id}/history`); asks unless `--yes` |
| `server jobs history rm <name\|id> <run-id> [--yes]` | `jobs:manage` | Deletes one run (`DELETE /{id}/history/{execId}`); asks unless `--yes` |

History retention is also a setting: `config get jobs` / `config set jobs.history.<key> <value>` (core `config` command).

## Install from the dashboard or in code

The package declares an entry point (`JobsPluginEntry`, plugin id `jobs`), so it can be installed from the dashboard's
marketplace (the admin plugin's server plugins, loaded at the next start) or added in code through the same entry:

```csharp
cli.AddPlugin<JobsPluginEntry>();
```

Its settings (config provider `jobs` unless noted; code options are their defaults, saved settings override them):
none of its own: the `jobs` config provider (history retention) is the scheduler's; runs are in memory unless jobs-efcore sets a storage. A required setting without a value lists the plugin as `needs-configuration` with `missing`; see
[Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard).

## License

MIT
