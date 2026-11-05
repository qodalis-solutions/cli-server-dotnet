# Change Log

Notable changes to the .NET CLI server packages (`Qodalis.Cli`, `Qodalis.Cli.Abstractions`, `Qodalis.Cli.Plugin.*`).
Every package shares one version with the Node and Python servers.

## 1.0.0-beta.16

### Audit plugin

- The audit log is a plugin of its own, **Qodalis.Cli.Plugin.Audit** (`cli.AddAudit()`): the entries, the in-memory
  store and `GET/DELETE /api/v1/qcli/audit`, listed on the dashboard's Plugins page and shown as its Audit page. The
  users plugin adds it (`AddUsers`), so existing hosts keep their audit log; a host can use it without the users
  plugin. See [its README](plugins/audit/README.md).
- The contract moves to **Qodalis.Cli.Abstractions** (`Qodalis.Cli.Abstractions.Audit`: `AuditEntry`,
  `AuditMetadata`, `AuditQueryOptions`, `IAuditStore`), so a store of your own needs only the abstractions.
  `InMemoryAuditStore` is in `Qodalis.Cli.Plugin.Audit`; `AuditEvents` (the users plugin's event names) stays in the
  users plugin. **Breaking**: update the `using`s of code that implements or calls `IAuditStore`.
- `cli.SetAuditStore<T>()` / `SetAuditStore(instance)` keep the log elsewhere; `UsersModuleOptions.SetAuditStore`
  still works and does the same.
- The route policy protects the audit API itself (`audit:read` for `GET`, `audit:clear` for `DELETE`); clearing
  records `audit_cleared` with the caller.
- **Qodalis.Cli.Plugin.Audit.EfCore** replaces Qodalis.Cli.Plugin.Users.EfCore (published in 1.0.0-beta.15 only):
  the same `AddEfCoreAuditStore(...)`, `ApplyAuditStorageModel()` and `audit_entries` table, now in the namespace
  `Qodalis.Cli.Plugin.Audit.EfCore`. Change the package and the `using`; the data stays as it is.
- The admin status reports the `audit` feature when the audit plugin is installed.

### Admin

- The admin plugin ships dashboard 1.0.0-beta.13: the Terminal page is shown on every server again (it was hidden
  without an OS shell).

## 1.0.0-beta.15

### Installed plugins

- The admin plugin lists the installed server plugin packages at `GET /api/v1/qcli/plugins/installed` (`id`,
  `name`, `version`, `description`), shown on the dashboard's Plugins page. The built-in plugins list themselves
  when added; a plugin of your own calls `cli.AddInstalledPlugin(assembly)`. See
  [Installed plugins](plugins/admin/README.md#installed-plugins).
- The admin status reports the `audit` feature when the users plugin is installed (the dashboard's Audit page).

### Audit log

- Audit entries gain `Target` (what the event is about when it is not a user; the group events set `group:<name>`)
  and `Properties` (the host's own values). `GET /api/v1/qcli/audit` and `AuditQueryOptions` gain `actor` and
  `offset` for filtering by who did it and paging.
- `IAuditStore.PruneAsync(before)` deletes old entries (default: keeps everything; the in-memory store implements it).
- `GET /api/v1/qcli/audit` without a `limit` returns the newest 100 entries (it returned all), and at most 1000.
- New package **Qodalis.Cli.Plugin.Users.EfCore**: the audit log in any EF Core database
  (`cli.AddEfCoreAuditStore(o => o.UseNpgsql(...))`), created on first use, or in the host's own migrations with
  `modelBuilder.ApplyAuditStorageModel()`. It keeps every entry, failed sign-ins included: prune it on a schedule
  (see [its README](plugins/users-efcore/README.md)).

### Jobs

- Job ids are stable: `CliJobOptions.Id`, by default the job's name (`name-2`, ... for duplicates). They were random
  on every start, so saved pauses and history never found their job again after a restart. Ids that were random
  before are not migrated. An id may not contain `/`, `\`, `?` or `#` (they are part of the jobs API's URLs); a
  default id replaces them with `-`.
- A job's saved state (a pause or stop from the API) is kept across restarts, except that `Enabled = false` in the
  code always keeps the job stopped.
- Interval jobs keep their rhythm across restarts and pauses: the next run is the last run plus the interval (right
  away when that has passed). `CliJobOptions.RunOnStart` runs a job once right after the scheduler starts.
- Inside a job, `ICliJobExecutionContext` gains `Properties` (the run's own values, saved and returned by the API),
  `Services` (a service scope per run) and `Discard()` (leave a completed run that found nothing to do out of the
  history). Implementers of `ICliJobExecutionContext` must add them.
- `GET /api/v1/qcli/jobs/executions`: executions across jobs, newest first, filtered by job ids, group, status and
  `since` (without an offset it is UTC); `ICliJobManager.QueryExecutionsAsync` and
  `ICliJobStorageProvider.QueryExecutionsAsync`, whose defaults merge the per-job listings (the scheduler's manager
  names its jobs, so a store needs no query of its own; a manager that cannot list answers 501). Executions carry
  `properties` in the API and the `jobs_history` MCP tool; read back from a database they are JSON values.
- A run whose properties cannot be saved (a value that does not serialize) is saved without them and with an error
  line, instead of staying "running". Run-on-start jobs start off the host's startup path, each run's service scope
  ends with the run (before a retry's delay), and a run's failures are logged instead of lost.
- `ICliJobStorageProvider` gains `PruneAsync(DateTimeOffset before)` and `DeleteExecutionAsync` (defaults keep
  everything); `ICliJobScheduler` exposes the in-process scheduler for the host's own code. Its `Jobs` are read-only:
  `JobRegistration`'s state setters are internal now.
- **Breaking** for code that constructs them: `SchedulerJobManager` takes an `ICliJobScheduler` (was
  `CliJobScheduler`, which implements it, so source compiles unchanged but binaries built against beta.14 need a
  rebuild). `CliJobScheduler` keeps its three-argument constructor and gains one with an `IServiceScopeFactory`.
- `Qodalis.Cli.Plugin.Jobs.EfCore` creates its tables inside an existing database too (it did nothing when the
  database had other tables; once, even when the first calls come together, and next to another instance creating
  them), saves the executions' properties, and offers `modelBuilder.ApplyJobStorageModel()` for
  host migrations. `AddEfCoreJobStorage` before the first `AddJob` is no longer replaced by the in-memory store.

## 1.0.0-beta.14

### Jobs that run elsewhere

- `ICliJobManager` (`Qodalis.Cli.Abstractions.Jobs`): the jobs API (`/api/v1/qcli/jobs`), the `jobs_*` MCP tools and
  the dashboard's jobs page work on it instead of the in-process scheduler. The default `SchedulerJobManager` runs the
  jobs registered with `AddJob`; `cli.SetJobManager<T>()` plugs in a host's own jobs (a worker service, a queue).
  Actions a manager does not offer answer `501` (`NOT_SUPPORTED`), and jobs list the actions they offer in `actions`.
  See [Jobs that run elsewhere](plugins/jobs/README.md#jobs-that-run-elsewhere).
- The admin status counts the jobs (`registeredJobs` was always 0).

### Filesystem

- The admin status reports the `filesystem` feature only when `AddFileSystem()` enabled it, so the dashboard no
  longer shows a Files page on every server; `/api/qcli/fs` answers `404` without `AddFileSystem()` instead of
  serving an in-memory store. Hosts that used the filesystem API without calling `AddFileSystem()` must call it.

### Dashboard name and icon

- `AddAdmin(o => o.Branding...)`: the name and icon an application ships the dashboard under, and its terminal's
  name (`Name`, `IconUrl`, `CliName`), served in `/qcli/admin/theme.json` and validated at startup. Defaults:
  Qodalis Cli Admin with the Qodalis Solutions logo. See [Name and icon](plugins/admin/README.md#name-and-icon).
