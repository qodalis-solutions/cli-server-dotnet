# Change Log

Notable changes to the .NET CLI server packages (`Qodalis.Cli`, `Qodalis.Cli.Abstractions`, `Qodalis.Cli.Plugin.*`).
Every package shares one version with the Node and Python servers.

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
