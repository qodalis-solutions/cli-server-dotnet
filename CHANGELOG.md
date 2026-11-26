# Change Log

Notable changes to the .NET CLI server packages (`Qodalis.Cli`, `Qodalis.Cli.Abstractions`, `Qodalis.Cli.Plugin.*`).
Every package shares one version with the Node and Python servers.

## Unreleased

- **Server plugins from code or the dashboard** (the same contract, statuses, load order and JSON as the Node and Python servers). Entry points: `[assembly: CliPlugin(typeof(TEntry), Id, Order)]` (also on an entry class) with `ICliPluginEntry.Register(CliPluginContext)`; the context gives the builder, services, logger, data directories, restart service, source (`code`|`marketplace`), code options, `Settings(...)` (a `CliStoredConfigProvider`), `BootSettings(...)` (settings kept in `<data dir>/plugin-settings.json` and read while the services are registered) and `NeedsConfiguration(...)`. `cli.AddPlugin<TEntry>(options)` runs the same entry in code, its options the defaults of the settings (origin `code`; saved settings override them). Installed plugins: `<data dir>/server-plugins/installed.json` (data dir `qcli-data` in the working directory, `cli.ConfigureServerPlugins(o => ...)`), each package loaded at startup in a collectible `CliPluginLoadContext` that resolves what the host has (`Qodalis.Cli*`, the framework, its packages) from the host. Code and installed plugins register together after the host's own, in their declared order (`CliPluginOrder`: Auth 10, ConfigStore 20, Core 30, Default 50, Source 70; users-efcore 5, portal 60). Statuses `loaded`, `failed` (rolled back, logged, kept in `installed.json`; startup goes on), `skipped` (the host has the package), `needs-configuration` (a required setting without a value, with `missing`); states, records and the installed plugins (`GET /plugins/installed`) carry `status`, `missing`, `settings` and `source`. Settings: `Required` on settings and entries (`required` in the API), `CliStoredConfigProvider.Missing()`, entries' `origin` (`code`, `setting`, `default`), source `code`; plugin settings in `<data dir>/plugin-settings.json` (0600) while the server keeps settings in memory. New permission **`plugins:install`** (core catalog, group Server).
- **Every official plugin except admin has an entry** reading its options from settings: weather, aws, docs (served without `UseQodalisDocs`), audit, the filesystem (in the server) and its JSON, SQLite, S3 and EF Core storages (`DeferredFileStorageProvider`, `ReadOnlyFileStorageProvider`, read-only by default), the data explorer (in the server: rules for the later sources) and its PostgreSQL, MySQL, SQL Server, Redis, MongoDB, Elasticsearch and SQLite sources (built when the data explorer is first used, none until configured), users (its middleware first in the pipeline), users-efcore, audit-efcore, jobs, jobs-efcore, public-api (needs users; adds the `public` OpenAPI document when the host has none), public-api-efcore, portal (needs docs or public-api), databases (the ADO.NET driver of its dialect found at runtime), config-redis and config-efcore (the default config store; their own settings never in it) and logs-seq. The EF Core stores' entries take `dialect` (sqlite, postgres, mssql; the provider found at runtime), `connectionString` and `tablePrefix`.
- **Server plugins from the marketplace** (admin plugin; the same routes and JSON as the Node server): `/api/v1/qcli/server/marketplace` (`GET /`, `/plugins`, `/plugins/{name}`, `/installed`; `POST /installed`, `PUT`/`DELETE /installed/{name}`: 202 with `restartRequired`), the .NET package of `kind=server` plugins: the `.nupkg` from the NuGet flat container (`DashboardMarketplaceOptions.NuGetFeed`) checked against the version record's sha512, its nuspec, the `lib/<tfm>` folder for this runtime, `[assembly: CliPlugin]` and `QodalisCliAutoInstall` read without loading it, the server range; dependencies the server lacks only when the version record pins them with a sha512 (`dependencies: [{ id, version, integrity }]`) or the host trusts them (`TrustedDependencies`), never an assembly the host has; extracted into `<data dir>/server-plugins/<package>/<version>/`, a restart marked (`plugin:<name>`), audit `server.plugin.install|update|remove`, event `server:plugins-changed`. Terminal: `admin plugins search|installed|install <name>[@version]|update|remove`.
- **Restart required and Restart server** (the same routes, JSON, permission, audit, events and command as the Node server). Core keeps the changes waiting for a restart (`ICliServerRestart` / `CliServerRestart` in the container: `MarkRequired(new(id, source, description))`, idempotent by id, `ClearRequired(id)`, empty on every start); the config service marks a changed `RequiresRestart` entry as `config:<provider>.<section>.<key>` and clears it when the value is set back. `cli.ConfigureRestart(o => ...)`: `Mode` `auto` (default; `exit` when PM2, systemd, IIS, Kubernetes or Docker is detected, else not supported), `exit` (`IHostApplicationLifetime.StopApplication()` with `Environment.ExitCode` 75, `ExitCode`), `self` (once the host stopped, `Environment.ProcessPath` with the original arguments again, `dotnet <entry dll>` under the dotnet host, same environment and working directory), `handler` (`Handler`, once the host stopped); `AllowAnonymous`, `ShutdownTimeout` (10 s, the host's shutdown timeout during a restart), `Runtime` (`ICliServerRestartRuntime`, so tests never stop their host or start processes). IIS counts as a supervisor (an `ASPNETCORE_IIS_*` variable or the `w3wp`/`iisexpress` in-process host). Graceful shutdown: WebSockets closed with 1012, `OnShutdown(name, fn)` hooks, then the host's stop (in-flight requests, hosted services such as the job scheduler, the container's disposables). `GET /api/v1/qcli/server/restart` (`admin:access`): `{ supported, mode, supervisor, reason?, startedAt, pending: [{ id, source, description, since }] }`; `POST` `{ reason? }` (new permission **`server:restart`**, core catalog, group Server, `CliRoutePolicy.ServerRestart`): 202 `{ restarting: true, pending }`, 409 when not supported or already restarting, 403 on a server without sign-in unless `AllowAnonymous`. Audit `server.restart`, events `server:restart-required` and `server:restarting` (audience `admin:access`). Admin plugin: `admin restart [--yes] [--reason]` and `admin restart status [--json]`. `BroadcastDisconnectAsync(closeCode, reason)` on the event and log socket managers. The demo restarts itself (`Mode = self`).
- **Dashboard plugins from the marketplace** (the same routes, JSON, checks and command as the Node server). The admin plugin lists the marketplace's `kind=dashboard` plugins and installs, updates and removes them at runtime, on by default: `AddAdmin(o => o.Marketplace = new() { Url = "https://plugins.qodalis.com", Install = "verified" | "listed" | "off", Dir = "<content root>/qcli-data/dashboard-plugins", HttpHandler })` (`Enabled = false` turns it off). Routes under `/api/v1/qcli/dashboard/marketplace`: `GET /` (`{ marketplaceUrl, install, dashboardApi: 1 }`), `GET /plugins?query=&page=&pageSize=` (fetched with `kind=dashboard&dashboardApi=1`, other kinds dropped) and `GET /plugins/{name}` (the marketplace's items with `installed`, `updateAvailable`, `allowed`, `blockedReason`, `requires: { serverPackage, installed }` for the NuGet package; 502 `marketplace unreachable`), `GET /installed`, `POST /installed` `{ name, version? }` (201; the newest listed version with `dashboardApi` 1 when none is given), `PUT /installed/{name}` (keeps the page id), `DELETE /installed/{name}`. Bundles come from jsDelivr, then unpkg (5 MB each), and are checked against the marketplace's sha384 before anything is stored (a mismatch is a 502); they are kept in `<dir>/<name>/<version>/` (`@scope/name` as `@scope+name`) with `<dir>/installed.json` written atomically, registered as dashboard extensions at once (`GET /dashboard/extensions` marks them `source: "marketplace"`; an id already registered is a 409), served at `/qcli/modules/<id>/extension.js|css` with an ETag, and registered again at startup after checking each file's hash. Changes run one at a time, need the new permission **`dashboard:install`** (core catalog, group Server, `CliRoutePolicy.DashboardInstall`), are audited as `dashboard.plugin.install|update|remove` and published as `dashboard:extensions-changed` (audience `admin:access`). Terminal: `admin dashboard [list|search <query>|install <name>[@version]|update <name>|remove <name>]`.
- **Settings of dashboard plugins** (the same JSON, checks and command as the Node server). A dashboard plugin's version may declare settings (`config: { entries }` of its marketplace record: the config entry model without `secret`, keys `^[a-zA-Z][a-zA-Z0-9_.-]{0,63}$`, at most 50 entries, defaults passing their own validation, at most 32 KB); the admin plugin checks them again (`DashboardPluginConfigs.Parse`) and installs a version with a secret or invalid settings without settings and `warnings` in the response. New config provider **`dashboard-plugins`** (`DashboardPluginsConfigProvider`, `DashboardMarketplace.Config`, writable, registered at startup when the marketplace is on): one section per installed plugin with settings (the page id, the plugin's title), the installed version's entries with their defaults, in the host's store for `dashboard-plugins`, else the default store; changed through the config API, `config set dashboard-plugins.<id>.<key>` and MCP with the usual validation, versions, audit and `config:changed`. `POST /installed` and `PUT /installed/{name}` take `config: { key: value }`, checked before anything is downloaded (400 `{ error: 'validation failed', errors }`; install-time values need `dashboard:install`); an install clears values left from an earlier install of the page, an update keeps the stored values the new version still accepts and deletes the others (`configDropped` in the response), a removal deletes the section's values, `installed.json` keeps each version's `config` so the sections come back after a restart, offline. New `GET /installed/{name}/config` (`admin:access`): `{ values, version }`, the values stored over the defaults. Installed plugins carry `hasConfig`; audit entries `config` and `configDropped`; `config:changed` when an install, update or removal changes stored values. Terminal: `admin dashboard install|update ... --config '<json>'` and `admin dashboard info <name> [--json]` (the page, files and settings with their values).
- Core: `CliDashboardExtensions` is the runtime registry of the dashboard's pages (`Add`, `Remove`, `Contains`; `GET /dashboard/extensions` reads it on every request).
- **Config providers** (#97, cli-server-dashboard#61; the same contract and JSON as the Node and Python servers). The server's configuration is read and changed through providers that the server, plugins and the host register (`cli.AddConfigProvider<T>()` / instance / factory, a module's `ConfigProviders`): `ICliConfigProvider` (read) and `ICliConfigWriter` (write) in `Qodalis.Cli.Abstractions.Config`, entries typed `string`, `number`, `boolean`, `string[]`, `enum`, `secret`, `json`, `duration` or `url`, with sources, `mutable`, `requiresRestart`, `sensitive`, validation (`min`, `max`, `pattern`, `options`, `required`) and an `editor` hint. `CliStoredConfigProvider` describes settings in code and keeps changes in the server's config store: `InMemoryConfigStore` (default), `JsonFileConfigStore`, or the new `Qodalis.Cli.Plugin.Config.EfCore` (`AddEfCoreConfigStore`, table `QcliConfig`); `cli.SetConfigStore`, `cli.ConfigureConfig`. Built-in providers: `server`, `host` (server options, an allowlist of environment variables and `IConfiguration` keys, secrets masked), `cors` (allowed origins at runtime), `mcp` (tool lists, instructions, read-only), `filesystem` (read-only), `custom`, and from plugins `auth` and `logs` (admin), `jobs` (history retention) and `users` (sessions, lockout, passwords).
- **The admin plugin's config API is converted**: `GET /api/v1/qcli/config` answers `{ providers }`, `GET /config/{provider}` one provider, `PATCH /config/{provider}/{section}` with `{ changes, version }` changes a section (400 with per-key errors, 409 with the current version when stale). The former `PUT` (`UpdateConfigRequest`) and `AdminConfig.GetConfigSections()` are gone. Secrets are never returned (`value: null`, `isSet`); `null` resets a setting.
- **Permissions `config:read` and `config:write`** in core's catalog and route table (plus a provider's `RequiredPermission`); every change is audited as `config.update` (old and new values, secrets as `***`) and published as `config:changed` (audience `config:read`).
- **`config` command** (`list`, `get <provider>[.<section>[.<key>]]`, `set <provider>.<section>.<key> <value> [--expected-version v]`, `reset`) and **MCP tools** `config_list`, `config_get`, `config_set`. New `McpOptions.ReadOnly`: only tools annotated read-only are listed and callable; `Tools`, `Exclude`, `Instructions` and `ReadOnly` change at runtime.
- The library's default CORS policy is always registered and follows the live allowed origins; `LogRingBuffer.Resize`/`MinLevel`; `CliJobScheduler.SetHistoryDefaults`; the users plugin's session, lockout and password settings change at runtime.
- **Plugin terminal commands** (the same syntax, permissions and output as the Node server). Admin plugin: `admin status [--history [--minutes N]]`, `admin plugins [list|enable|disable <id>]` (idempotent, the admin module itself not disabled), `admin clients`, `admin logs [--level] [--search] [--service] [--since 5m|1h|7d|<ISO>] [-n N]` over the configured `ILogStore` (the in-memory history, or Seq with `Qodalis.Cli.Plugin.Logs.Seq`) (`admin:access`; `logs` also `logs:read`); an `admin` module describes the command. Databases plugin: `databases runs [<db>] [-n N]` (`databases:read`). Docs plugin: `docs list [--group]`, `docs read <slug>` (aliases `show`, `cat`; no permission, like the pages). Public-api plugin: `api usage [--user <name|id>] [--days N]` (your own; `tokens:read` for another account), also with `AddPublicApiUsage`. Lists take `--json`. Shared services instead of copies: `ServerStatus` (`GET /status`, which now also answers `cpuUsagePercent`, the latest sample's CPU), `StatusHistorySummary`, `ModuleRegistry.SetEnabled`, `AdminQueryTime.ParseSince`, `DocsPages` (the page list and page view of `GET /pages`).
- **AWS permissions**: `aws:read` on the `aws` command, `aws:write` on `s3 cp|rm|mb|rb`, `ec2 start|stop|reboot`, `lambda invoke`, `sqs send|purge`, `sns publish` and `configure set`, in the catalog under group `AWS` (`AwsPermissions`).
- **`ICliModule.Permissions`** (`CliModulePermission`): a module's permissions, added to the catalog by `AddModule` (surface `cli`; a name already there stays as registered).
- **The machine's memory in the admin status** (as on the Node server): `GET /api/v1/qcli/status` and `admin status --json` add `systemMemoryTotalMb` (the RAM the server can use: a cgroup limit below the installed RAM, else the GC's container-aware `TotalAvailableMemoryBytes` when lower, else the installed RAM), `systemMemoryUsedMb` (the cgroup's usage when limited, else total minus available: `/proc/meminfo` on Linux, `GlobalMemoryStatusEx` on Windows, `sysctl` page counts on macOS; no new package) and `memoryLimited`; each status history sample adds `systemMemoryUsedMb` and `systemMemoryTotalMb`. Null when unknown. `admin status` shows `Memory      146 MB (process) · machine 9.8 GB of 16 GB used (61%)` (or `container 300 MB of 512 MB used (59%)`).
- **A module's commands carry its name**: `GET /api/v1/qcli/commands` lists a command added with `AddModule` under the module's name (`module`) unless the command names one itself (`CliProcessorModules`, a singleton the builder registers), so `aws` shows `module: "aws"`. The demo's example commands (`echo`, `status`, `time`, `hello`, `math`, `system`, `hash`, `base64`) are the `demo` module (`DemoModule`): the dashboard's Plugins page lists them as the demo plugin, which can be disabled like the others.
- **Commands list their permissions** (the same JSON as the Node and Python servers): `GET /api/v1/qcli/commands` gives every command and every sub-command in `processors` the permissions it declares itself (`RequiredPermissions`) as `requiredPermissions`, e.g. `aws:read` on `aws` and `aws:write` on `aws s3 rm`; left out when there are none. The parents' permissions and `commands:execute` are not repeated. The dashboard shows them on the Commands and plugin details pages.
- **Breaking:** the `echo`, `status`, `system`, `hash` and `base64` processors are no longer shipped outside the demo app (removed from `Qodalis.Cli.Server`; the demo keeps them in its `demo` module). `http` moves into `Qodalis.Cli` as `Qodalis.Cli.Processors.CliHttpCommandProcessor` (module `core`; hosts still add it with `cli.AddProcessor<CliHttpCommandProcessor>()`) and now needs the new core permission **`http:request`** (group Commands, "Make HTTP requests from the server"): refused with 403 on a protected server without it.
- **Where settings come from and where changes are saved** (the same JSON as the Node server). `GET /api/v1/qcli/config` adds `store` (`{ kind: memory|file|database|custom, title, detail, persistent }`) and every provider view `storage` (`{ kind: store|runtime|options|environment|configuration|custom, description, sources }`). Stores describe themselves with `ICliConfigStore.Describe()` (a default interface method returning null): `InMemoryConfigStore` `In memory` (not persistent), `JsonFileConfigStore` `JSON file` with the path relative to the working directory (or the file name only), `EfCoreConfigStore` `Database` with the EF Core provider, database name and table (`SQL Server database qcli, table QcliConfig`), never the connection string or credentials; a store without it is a `custom` store named after its class (`CliConfigStoreHelpers.Describe`, `DescribeLine`). Providers declare the optional `Storage`: `CliStoredConfigProvider` `store`, `server` `runtime`, `auth` `options`, `host` `configuration` with its `IConfiguration` sources in order, file names only (`Server options`, `appsettings.json`, `appsettings.{Env}.json`, `User secrets`, `Environment variables`, `Command line`; `CliHostConfigProvider.ConfigurationSources`); others `custom`. `ICliConfigService.DescribeStore()`; `config list` starts with `Store: JSON file (config.json), kept across restarts` (or `Store: In memory, changes are lost on restart`) and shows read-only providers' sources; MCP `config_list` returns `store` and each provider's `storage`.
- **The portal plugin's `portal` command and config provider** (the same JSON, output and permissions as the Node server). `portal` (= `portal status`) `[--json]`, `config:read`: the mount, whether the site was found and where from (`SitePath`, `@qodalis/cli-server-portal`, the sibling checkout or the embedded copy; `PortalSiteSources`), the sections (docs with its page count, the API's title and OpenAPI URL) and the current branding; `--json` is `portal.json` with `docs.pageCount` and `site: { path, found, source, dir }`. The `portal` config provider: `branding` (`name`, `logoUrl`, `themeMode`, `footerLinks`; the options' values are the defaults, changes apply to the next `/portal.json` without a restart) and the read-only `site` (`path`, `siteFound`, `sections`); the theme colours stay in code. `AddPortal` registers both, a `portal` module and `PortalState` (the current branding); the docs plugin's `CliPortalSection` carries `PageCount`.
- **A config store per provider** (the same JSON as the Node server). Every provider may keep its settings in its own store, some in `appsettings.json`, some in a database, some in Redis; the store a provider gets, first match wins: the host's store for its id (`cli.SetConfigStore(store, id)`, `SetConfigStore<TStore>(id)` resolved from the container, `SetConfigStore(sp => ..., id)`, `ConfigureConfig(o => o.Stores[id] = store)`, `CliConfigRegistry.SetStore(store, id)` until that provider registers, then it throws), the provider's own (`CliStoredConfigProviderOptions.Store`, `CliStoredConfigProvider.SetStore`, `CliCustomConfigProvider.Create(store:)`, `ConfigureJobs(o => o.ConfigStore)`, `AddUsers(o => o.ConfigStore)`, `AddPortal(o => o.ConfigStore)`, `AddAdmin(o => o.LogsConfigStore)`), the default store (`SetConfigStore(store)`, `SetConfigStore<TStore>()`), memory. **Breaking (JSON):** `GET /api/v1/qcli/config` answers `defaultStore` (was `store`) and every stored provider its `store` after `storage` (also `/config/{provider}` and MCP `config_list`/`config_get`); `ICliConfigService.DescribeDefaultStore()` (was `DescribeStore()`), `CliConfigRegistry.DefaultStore` (was `Store`), `StoreOf`, `Stores`. `config list` starts with `Default store: ...` and ends each stored provider's line with `· <store>`. The audit entry `config.update` has `properties.store` and the `config:changed` event `store` (the store's kind); a provider that saves itself has neither. New store kind `redis`; `ICliConfigStore.ReadyAsync` (optional), awaited by the config ready; `CliConfigStoreHelpers.DescribeName`.
- **`JsonFileConfigStore` in a section of a settings file** (`new JsonFileConfigStore(path, new() { Section = "qcli" })`, nested as `app.qcli` or `App:Qcli`): the file is re-read on every write and only the section replaced, the rest kept with its order, indentation, line endings, BOM and mode; described as `JSON file (config/app.json, section qcli)`. **`AppSettingsConfigStore`**: the settings in the app's `appsettings.json` (or `appsettings.{Environment}.json`), section `Qcli:Config`, each section with its version, so a host whose `IConfiguration` reloads on change sees them; described as `appsettings.json (section Qcli:Config)`; `cli.SetAppSettingsConfigStore(providerId?, configure?)` in the host's content root.
- **New plugin `Qodalis.Cli.Plugin.Config.Redis`**: `RedisConfigStore` keeps a provider's settings in Redis (StackExchange.Redis; a `redis://`/`rediss://` URL or configuration string, a shared `IConnectionMultiplexer`, or an `IRedisConfigClient`), one hash per provider section (`qcli:config:<provider>:<section>`: values as JSON, version, updatedAt), writes checked by the Node server's Lua script (409 on a stale version, retried without one), `TtlSeconds`, `MaxRetries`, described as `Redis (cache:6379/0, prefix qcli:config)` without credentials; `cli.AddRedisConfigStore(configure, providerId?)`. `AddEfCoreConfigStore` takes a `providerId` too.
- The demo keeps its settings in several stores: the JSON file by default, the `demo` provider in `appsettings.json`, `jobs` in a SQLite database through EF Core (`QCLI_DEMO_DB`), `custom` in Redis with `QCLI_DEMO_REDIS_URL`.
- Every plugin README has a `Terminal commands` section (server-side commands, or the web CLI plugin commands that drive the plugin).

## 1.0.0-beta.31

- The admin plugin embeds dashboard 1.0.0-beta.28: plugin pages no longer change the dashboard's layout (the modules' styles are scoped to their own elements, so Server resources, Events and the other pages keep their columns); the portal plugin serves `@qodalis/cli-server-portal` 1.0.0-beta.28.

## 1.0.0-beta.30

- The admin plugin embeds dashboard 1.0.0-beta.27 (plugins' pages as modules, Users and Account pages, a forced password change at sign-in, Qodalis CLI 2.8.0 in its Terminal); the portal plugin serves `@qodalis/cli-server-portal` 1.0.0-beta.27.
- **Users and Account pages.** `AddUsers` serves the dashboard's Users page (users, groups, sessions; `users:read`, at `/qcli/modules/users`) and Account page (the signed-in user's profile and password change; every user, at `/qcli/modules/account`), both from `@qodalis/cli-dashboard-module-users` embedded in `Qodalis.Cli.Plugin.Users`, also when the host application signs users in. `CliDashboardModule.Build` lets a page use another module's build (Account uses `users`) and follow its `ConfigureDashboardModule` override. New dashboard extension group `account`: listed in the dashboard's account menu (the signed-in user's menu) instead of the sidebar.
- **Job history on large tables** (#96). `Qodalis.Cli.Plugin.Jobs.EfCore` indexes the executions on `StartedAt`, `(JobId, StartedAt)` and `(Status, StartedAt)`; tables of an earlier version get the indexes on the store's first start, or through migration `2` of the `qcli-jobs` source with the databases plugin (hosts with `ApplyJobStorageModel()` in their own model get them in their next migration). Lists (`GetExecutionsAsync`, `QueryExecutionsAsync`) no longer load the log entries: runs carry `JobExecution.LogCount` instead (`LogEntryCount` reads either), `JobExecutionQuery.IncludeLogs = true` loads them, and `GetExecutionAsync` (the run detail endpoint, `server jobs logs`, the dashboard's log panel) always has them. The API's `logCount` is unchanged; `GET /jobs/stats` reads the runs without their logs. The scheduler's restart recovery asks for the logs it needs. New `ICliJobStorageProvider.SummarizeExecutionsAsync(since, jobIds?)`: per job and status, the number of runs and the start of the newest one (`JobExecutionSummary`), a `GROUP BY` in the EF Core store, in memory in the in-memory store, and by default from `QueryExecutionsAsync`. `QcliEfStoreSchema.EnsureIndexesAsync` / `IndexUpgrade<TContext>` (databases-efcore) create a store's missing indexes with the provider's SQL, and `CliSqlSchema.IndexExistsAsync` checks an index on every dialect.
- **The dashboard's plugin pages move into their plugins.** Jobs, Audit, API tokens, Filesystem and Data explorer are dashboard modules (`@qodalis/cli-dashboard-module-<id>`) embedded in the package that owns their API and served at `/qcli/modules/<id>`: `Qodalis.Cli.Plugin.Jobs` (`AddJob`), `Qodalis.Cli.Plugin.Audit` (`AddAudit`), `Qodalis.Cli.Plugin.Users` (`AddUsers`), `Qodalis.Cli` (`AddFileSystem`, `AddDataExplorerProvider`). `cli.AddDashboardModule(module, assembly | fileProvider)` serves a module's files outside the API (no-cache) and registers it with hashed URLs; `cli.ConfigureDashboardModule(id, fileProvider | null)` serves another build or leaves the page out. The Databases page uses it too. `build/Qcli.DashboardModules.targets` copies a module's build from the sibling cli-server-dashboard repo or the npm package and embeds it.
- **Dashboard extensions: `Widgets` and `Search`.** `Widgets = ["overview"]` shows a module's cards on the dashboard's Overview (jobs: active jobs, runs, failing jobs; databases), `["palette"]` adds its entries to the command palette (jump to a job); `Search = "/?q="` adds "Search <title> for …" to the palette.
- **Databases** (`Qodalis.Cli.Plugin.Databases`, new): the server's relational databases (PostgreSQL, MySQL/MariaDB, SQL Server, SQLite) with their stats and migrations. `cli.AddDatabases(d => d.AddDatabase("main", db => db.UseConnection(...).ApplyMigrationsOnStartup()).AddSqlFolder("main", "./migrations"))`. **Stats** (`GET /api/v1/qcli/databases/{db}/stats`): server version and uptime, size (data, indexes, log), connections (active, open, max), counters since the server started (queries, transactions, commits, rollbacks, rows read and written; `countersScope` database or server), cache hit ratio and the 10 largest tables; each group is read on its own and one the database user may not read is listed in `unavailable` (e.g. SQL Server without VIEW SERVER STATE). The list carries each database's `sizeBytes`. **Migrations**: versioned schema changes from a folder of SQL files (`NNNN_name.up.sql` / `.down.sql`, `-- qcli:no-transaction`) or code (`CliMigration`: SQL, SQL per dialect or code), applied under a lock per database (`pg_advisory_lock`, `GET_LOCK`, `sp_getapplock`), each in a transaction with its history row. History in `QcliMigrations` / `QcliMigrationRuns`; statuses `applied`, `pending`, `changed`, `missing`, `failed` (runs refused while one is changed or missing, unless `force`). API `/api/v1/qcli/databases` (`GET /`, `/{db}`, `/{db}/stats`, `/{db}/migrations`, `/{db}/migrations/runs`, `/{db}/migrations/sql`, `POST /{db}/migrations/up|down`; permissions `databases:read`, `databases:migrate`), terminal `databases` (alias `db`: `list`, `stats`, `migrations`, `up`, `down` with `--to`, `--dry-run`, `--force`), MCP tools `databases_list`, `databases_stats`, `migrations_status|runs|sql|up|down`, the dashboard's Databases page (`@qodalis/cli-dashboard-module-databases`, embedded, served at `/qcli/databases` and registered as a dashboard extension), the `databases:migration-run` event and `migrations.up` / `migrations.down` audit entries. The same plugin as on the Node and Python servers.
- **EF Core migrations in the databases plugin** (`Qodalis.Cli.Plugin.Databases.EfCore`, new): `d.AddEfCore<AppDbContext>("main", db => db.ApplyMigrationsOnStartup())` shows a DbContext's migrations (from `__EFMigrationsHistory`, which EF keeps), applies and rolls them back one at a time with `IMigrator`, previews them with `GenerateScript`, and replaces `MigrateAsync()` at startup (once across services starting together).
- **Plugin table names** (#99): the EF Core stores' tables are `Qcli` + PascalCase: `QcliAuditEntries` (was `audit_entries`), `QcliJobExecutions`, `QcliJobLogEntries`, `QcliJobStates` (`job_*`), `QcliApiTokens` (`api_tokens`), `QcliApiUsage` (`qcli_api_usage`), `QcliFiles` (`files`, filesystem-efcore and filesystem-sqlite, whose columns are PascalCase too). On the first start after the upgrade each store renames its old tables with their keys and indexes, keeping the rows. Store options `TablePrefix` (default `Qcli`), `Tables`, `Schema`, and `LegacyTableNames` to keep the old names (`QcliTableNameOptions`, `QcliTableNames.Resolve` in the abstractions). **Hosts that map the stores' tables in their own model** (`ApplyAuditStorageModel()`, ...) get the new names there: their next migration renames the tables, or they pass the old name (`ApplyAuditStorageModel("audit_entries")`) with `LegacyTableNames`. The stores' schemas are migration sources: `AddAuditStoreSchema`, `AddJobStorageSchema`, `AddApiTokenStoreSchema`, `AddPublicApiUsageSchema`; filesystem-sqlite versions its schema with the runner (source `qcli-filesystem`).
- **Plugin route permissions**: `cli.AddRoutePermission(path, read, write?)` declares the permissions a plugin's routes need on a protected server (`read` for GET/HEAD, `write` otherwise; the longest path wins, before the core table), kept in `CliServerOptions.RoutePermissions`.
- **Dashboard extensions** (cli-server-dashboard `docs/extensions.md`): `cli.AddDashboardExtension(new CliDashboardExtension { Id, Title, Titles?, Icon?, Group?, Order?, Permission?, Entry, Styles?, Version? })` adds a page to the admin dashboard, which loads `Entry` (an ES module the plugin serves) and mounts it. The admin plugin lists them at `GET /api/v1/qcli/dashboard/extensions` (`{ "extensions": [...] }`, registration order, unset optional fields left out, behind the dashboard's sign-in and `admin:access`, `Cache-Control: no-cache`). An invalid extension throws `ArgumentException` `invalid dashboard extension: <reason>`, a repeated id `InvalidOperationException` `dashboard extension already registered: <id>`.

## 1.0.0-beta.29

- The admin plugin embeds dashboard 1.0.0-beta.26: pick a new token's permissions from the server's list, host tokens marked, and a Requests today column when the server counts API usage. The portal plugin embeds `@qodalis/cli-server-portal` 1.0.0-beta.26.
- **Permission catalog.** The server keeps the permissions users can pick when granting them: `{ name, description, group, surface }`. Core registers the 11 route-policy permissions (`commands:execute`, `shell:execute`, `files:*`, `data:query`, `jobs:*`, `logs:read`, `admin:access`, `audit:*`) and the users plugin its 12 (`users:*`, `groups:*`, `sessions:*`, `tokens:*`). Hosts add their own with `cli.AddPermissions(p => p.Group("Rates").Add("rates:read", "Official exchange rates"))` (surface `host`); plugins pass `CliPermissionSurface.Cli`. `ICliPermissionCatalog` lists `cli` entries (core, then plugins) before `host` entries, whatever the call order. An invalid entry fails startup with `invalid permission entry: <name>`, a name registered twice (case-insensitive, also a plugin's name registered by the host, in either order) with `permission already registered: <name>`. The catalog is for choosing only: groups and `cli` tokens still accept any permission.
- **Users: `GET /api/v1/qcli/permissions`.** The catalog entries the caller holds (a scoped token: its scope); `?all=true` with `users:read` returns every entry (403 `permission denied: users:read` without it, 400 `all must be true or false` for other values).
- **Users: token `audience`.** Every API token is `cli` (default; existing tokens and stored rows without the field read as `cli`) or `host`. Token views carry `audience` (after `lastUsedAt`). `POST /tokens` accepts `audience: "cli"`; `"host"` answers 400 `host tokens can only be created by the host application`, other values 400 `audience must be cli or host`. Names and the 20-token limit count both audiences.
- **Users: host tokens and `IApiTokenManager`.** The host creates, lists and revokes tokens for its users: `CreateAsync`, `ListAsync`, `RevokeAsync`, `GetGrantablePermissionsAsync`, with the HTTP routes' validation, limits, audit entries and events. `host` tokens carry only `host` catalog entries the user holds (stored with the catalog's spelling; 400 `host tokens need permissions` / `not a host permission: <p>`, 403 `forbidden: cannot grant <p>`) and are accepted only by `[QcliAuthorize]` / `.RequireQcliAuthorization(...)`. Every users route and non-public CLI route and socket refuses them with 403 `forbidden: host tokens cannot use CLI routes`, whether or not `ProtectServer` is on; the shell socket closes with 4403 and that text. Any stored audience other than exactly `cli` is treated as host.
- **Shell:** a 403 recorded by the authentication plugin now closes the shell socket with the plugin's text (e.g. `password change required`) instead of `forbidden`, when it fits a close reason (123 bytes).
- **Users.EfCore:** `api_tokens.Audience` (nullable, `NULL` reads as `cli`). An existing table gets the column on first use, typed from the model for the provider; `AddEfCoreApiTokenStore(..., o => o.UpgradeSchema = false)` turns that off for hosts whose migrations own the table (the store then refuses to start until the column exists). See the package README for the upgrade order.
- `CliBuilder.AddPermissionsOnce(source, ...)` registers a plugin's entries once per builder, so calling `AddUsers` twice no longer duplicates them.
- **Users: the caller of a guarded route.** `[QcliAuthorize]` and `.RequireQcliAuthorization(...)` leave `HttpContext.GetQcliCaller()` (`QcliCaller(UserId, UserName, TokenId, Audience, Permissions)`) and, with `SetHostUser`, set `HttpContext.User` unless the host's authentication set one; token callers get the claims `qcli:token` and `qcli:audience` (`CliHostUser.TokenClaim`, `AudienceClaim`).
- **Users: host-token-only guards.** `[QcliAuthorize("rates:read", HostTokenOnly = true)]` and `.RequireQcliAuthorization("rates:read", hostTokenOnly: true)` refuse sessions, adapter callers and `cli` tokens with 403 `forbidden: a host token is required`, before the permission check. Both guards leave `IQcliAuthorizeMetadata` on the endpoint for OpenAPI.
- **Users: token usage.** With an `IApiTokenUsageProvider` registered (the public-api plugin), token views (`GET /tokens`, `IApiTokenManager.ListAsync`) end with `usage: { today, last30Days, days: [30 × { date, requests }] }`; a failing provider is logged and ignored. Unchanged without one.
- **Users: guard-only mode** (`o.UseGuardOnly()`): no users routes, no seeding, no sweeps, no server protection; the guards validate tokens over shared stores, for a second service. It requires `WithoutCliRoutes()`: startup fails with `guard-only needs WithoutCliRoutes()` otherwise. **Core: `cli.WithoutCliRoutes()`** leaves out every CLI route and socket (no controllers of core or plugins; `UseCli()` throws).
- **`Qodalis.Cli.Plugin.PublicApi`** (new package; **requires Microsoft.OpenApi 2**, so a host using Swashbuckle needs Swashbuckle 10 or later): `.RequirePublicApi("rates:read")` / `[RequirePublicApi]` guard the host's routes for `host` tokens, with per-account daily and per-minute quotas from the host's `IPublicApiQuotaResolver` (fixed UTC windows, revoked tokens count), `X-RateLimit-*` and `Retry-After` headers, 500 `internal server error` when the resolver throws; usage per token and day (in memory by default), `IPublicApiUsage` for account pages; `app.UseQodalisPublicApi()` serves the host's `AddOpenApi("public")` document at `/qcli/api/openapi.json` (bearer scheme `qcliToken`, `x-required-permission`, `x-host-token-only`, `servers`, where the host set none) and Swagger UI at `/qcli/api/swagger`.
- **Public API: usage without the API.** `cli.AddPublicApiUsage(o => o.UseQuotaResolver<T>())` adds only `IPublicApiUsage` and token `usage` over the usage store (with `AddEfCorePublicApiUsageStore` in either order), for the main service of a two-service host: no guard, no document, no portal section. `AddPublicApi` includes it.
- **Public API with users guards.** `RequirePublicApi` checks the permissions of the users guards on the endpoint before counting, and a refused request carries no caller even when a users guard admitted it first (`QcliAuthorization.Revoke`). Swagger UI and the document's `servers` follow the request's path base (`UsePathBase`); a path base starting with `//` or `/\`, or holding whitespace or a control character, answers 400 `invalid path base` (also in the docs and portal plugins). Every form of Swagger UI's index route is the plugin's page; other non-asset paths under `swagger/` answer 404. `AddPublicApi` and `AddPublicApiUsage` on one builder throw.
- **`Qodalis.Cli.Plugin.PublicApi.EfCore`** (new package): `cli.AddEfCorePublicApiUsageStore(...)` keeps the usage counters in `qcli_api_usage`, shared by every process (never more than the limit admitted); `modelBuilder.ApplyPublicApiUsageModel()` and `EnsureCreated = false` for hosts whose migrations own the table.
- **`Qodalis.Cli.Plugin.Docs`** (new package): `cli.AddDocs(o => o.AddFolder("docs"))` and `app.UseQodalisDocs()` serve markdown guides (front matter `title`, `group`, `order`; variables `{{ baseUrl }}`, `{{ name }}` and the host's) at `/qcli/docs/pages` and `/qcli/docs/pages/<slug>`.
- **`Qodalis.Cli.Plugin.Portal`** (new package): `cli.AddPortal(...)` and `app.UseQodalisPortal()` serve the prebuilt portal site (`@qodalis/cli-server-portal`) at `/qcli/portal` (or `/`, without shadowing the host's routes) with `portal.json`: name, logo, theme, footer links and the docs and API sections the other plugins register (`Qodalis.Cli.Portal.CliPortalSection`).
- Core: `Qodalis.Cli.Appearance.CliColor.IsValid`, the colour check of the admin theme (`AdminTheme.IsColor` uses it) and the portal theme.
- Demo: a public route `GET /v1/demo/time` (`demo:read`), guides in `demo/docs`, the portal at `/qcli/portal`; Swashbuckle 10 (Microsoft.OpenApi 2), schemas named by full type name (the demo's Swagger document failed on the two `LoginRequest` types). `Qodalis.Cli.Server` moves to Swashbuckle 10 too.
- **`WithoutCliRoutes()` now also removes the controllers MVC discovers on its own.** A Web SDK host lists every referenced assembly that uses MVC as an application part, so core's and the plugins' controllers (`/api/v1/qcli/commands`, users, admin, audit, jobs, ...) were routable in a guard-only service, some anonymously. `AddCli` now adds a controller feature provider that removes every controller of `Qodalis.Cli`, of `Qodalis.Cli.Plugin.*` assemblies and of plugin parts (`AddApplicationPart`); the host's controllers stay. Hosts no longer need to remove the `Qodalis.*` application parts themselves.
- Admin: without CLI routes (`WithoutCliRoutes()`) `UseQodalisAdmin()` serves nothing and logs a warning.

## 1.0.0-beta.28

- The admin plugin embeds dashboard 1.0.0-beta.24: history retention on the job page and the pruning job's last run, collapsible job filters, Jobs and Recent runs tabs, paged lists, tag colours, run controls and row actions as button groups, last-runs tooltips that open the run, a navigation drawer on phones and tablets, shared selects, menus and dialogs, plugin icons, and uploads into folders.
- **Jobs: built-in history retention** (#94). `cli.ConfigureJobs(o => o.History...)` or `Jobs:History:*` in configuration: `MaxAge`, `MaxRunsPerJob`, `FailedMaxAge`, `FailedMaxRunsPerJob` and `PruneInterval` (default 1 day), off by default. A job overrides them with `CliJobOptions.History` (or keeps everything with `KeepAll`). The built-in system job `jobs:prune-history` (group `system`, runs on start when due) deletes the runs past the limits and records how many in its run's `deleted` property. Failed runs are counted on their own when `FailedMaxRunsPerJob` is set; running runs and each job's newest run are never deleted; runs of jobs no longer registered follow the global limits. The job store gets `PruneAsync(jobId, retention, now)` and `GetExecutionJobIdsAsync()` (default implementations for other stores; set-based deletes in EF Core). Job info carries `historyRetention` (the limits in force, `null` when none apply), for the dashboard; a custom `ICliJobManager` fills it with `CliJobHistoryOptions.Resolve`.
- **Jobs: run ids in the statistics.** Each recent run in `GET /api/v1/qcli/jobs/stats` carries its execution `id`, so the dashboard's last-runs bars open the run.
- **Host commands see the caller of a linked terminal.** When the users plugin authenticates a CLI request itself (an API token, as a terminal linked with `server add` uses, a session or a WebSocket ticket), it also sets `HttpContext.User` (`CliHostUser`: `NameIdentifier`/`sub` = the user id, `Name`, `Email`, `qcli:permission` and `qcli:auth` = `token` or `session`), so the host's services that read the signed-in user see the caller: before, a host command run with an API token saw an anonymous request. Only CLI routes; a user the host's own authentication set is kept. `UsersModuleOptions.SetHostUser` (default `true`).
- **Filesystem:** an upload whose `path` is a folder (or `/`) answers 400 with a hint (the path is the file's full path, e.g. `/reports/rates.csv`) instead of a 500.
- **Icons for the installed plugins.** `/api/v1/qcli/plugins/installed` returns each package's `icon` (one emoji or an image URL, `null` when it has none), read from its `QodalisCliIcon` assembly metadata (`<AssemblyMetadata Include="QodalisCliIcon" Value="💳" />` in the plugin's project, `CliInstalledPlugin.IconMetadataKey`). Every package has one: Core 🖥️, Abstractions 🧱, Admin 🎛️, Audit 🛡️, Aws ☁️, Jobs ⏱️, Users 👥, Weather 🌤️, Logs.Seq 📜, FileSystem 📁 (Json 📄, Sqlite 🪶, S3 🪣), the data explorer providers (Sql 🪶, Postgres 🐘, Mysql 🐬, Mssql 🗃️, Mongo 🍃, Redis 🔴, Elasticsearch 🔍) and the EF Core stores 🗄️. The weather, aws and filesystem modules have icons too (`ICliModule.Icon`: 🌤️, ☁️, 📁).

## 1.0.0-beta.27

- The admin plugin embeds dashboard 1.0.0-beta.23: built with Qodalis CLI 2.5.2; New token dialog, SQL editor with completion, the query as run ("Ran as"), module icons on the Plugins page, and the return to the dashboard after an external sign-in.

- **Command modules on the Plugins page for commands registered on their own.** Commands added with `AddProcessor<T>()` (built by dependency injection, so they cannot live in a module instance) are listed under the module their `Module` property names, and the page's switch disables them too; before, only `AddModule` modules were listed. A module with the same name and no processors describes the group (description, version, icon). `ICliModule.Icon` (one emoji or an image URL, default `null`) is returned as `icon` by `/api/v1/qcli/plugins` for the dashboard's module cards.

- **API tokens and terminal links with an external auth adapter (opt-in).** `UsersModuleOptions.AllowApiTokens()` (`ApiTokensWithExternalAuth`): a host that signs users in itself (`UseExternalAuth`) can also accept the plugin's API tokens. `qcli_pat_` bearer tokens are checked before the adapter, users signed in by the host create tokens (`POST /tokens`) and approve terminal links (`POST /link/code/{code}/approve`); both answered 501 before and still do without the option. The token's user comes from the user store by id (it must find the adapter's users and report `Disabled`); keep tokens in a persistent `IApiTokenStore`.
- **`Qodalis.Cli.Plugin.Users.EfCore`** (new package): `cli.AddEfCoreApiTokenStore(o => o.UseNpgsql(...))` keeps API tokens (and the tokens of linked terminals) in a database through EF Core, so they survive restarts and are shared by every instance; table `api_tokens`, created on first use or by the host's migrations (`modelBuilder.ApplyApiTokenStorageModel()`). Expired tokens are deleted as tokens are listed.
- Admin: `AdminConfig.SignInUrl` documents how the dashboard (1.0.0-beta.23) sends users back after the host's sign-in: `{returnTo}` in the URL, or a `returnTo=` parameter, to open again with `#token=`.
- **Data explorer: names with capitals just work.** When a read-only SQL source answers that a table or column does not exist because the query wrote it without quotes (PostgreSQL folds such names to lower case: `SELECT count(*) FROM Users` looks for `users`), the query runs again with the name as the schema spells it, quoted the source's way: `"Users"` (PostgreSQL, SQLite), `[Users]` (SQL Server with a case-sensitive collation), `` `Users` `` (MySQL on a case-sensitive file system). The result's new `executedQuery` shows the query that ran, and the audit entry keeps both. Only a name that matches exactly one table the source shows or column it does not drop is changed, never inside strings or comments, nor a function name (`count(`). Off with `DataExplorerProviderOptions.FixNameCase = false`; writable sources (`ReadOnly = false`) are never run twice. Providers quote with `IDataExplorerProvider.QuoteName` (default ANSI double quotes). When nothing can be fixed, the error adds the hint `Names with capital letters need double quotes: "Users"`.
- Fix: the SQLite data explorer provider (`Qodalis.Cli.Plugin.DataExplorer.Sql`) left `PRAGMA query_only` on pooled connections, so a host using the same connection string (an app's EF Core context) could no longer write after a read-only query. It now opens its own connections without pooling.

## 1.0.0-beta.26

- The admin plugin embeds dashboard 1.0.0-beta.22: built with Qodalis CLI 2.5.1 (touch scrolling of the terminal page on phones, page no longer scrolls under the panel).

- Fix: data explorer sources with `AllowedObjects` or `MaskColumns` refused ordinary queries on quoted names, such as PostgreSQL's mixed-case tables: `SELECT "Id" FROM "Users" WHERE ...` answered `'WHERE' is not available on this source` (and `FROM "Users" u` named `u`). Under the MySQL reading, where `"..."` is a string, the string was dropped, so the next word was taken as the table. It now stays one name token, so the table is checked by its name, and a quoted masked column in `WHERE` is still refused.

## 1.0.0-beta.25

- The admin plugin embeds dashboard 1.0.0-beta.20: job tags, command metadata, the terminal link page, API tokens, a data explorer page and audit details.

- **Jobs: tags.** `CliJobOptions.Tags` (free-form labels such as `country:ro`, `bank`; each 1-64 characters without
  commas, at most 20, duplicates ignoring case dropped; rules in `CliJobTags`), `CliJobInfo.Tags`, always present in
  job info (`[]` when none). `GET /api/v1/qcli/jobs?tag=a&tag=b` (repeatable or comma-separated) lists the jobs that
  have every tag, ignoring case; `/executions` and `/stats` take `tag=` too. `PUT /jobs/{id}` accepts `tags` (replaces
  them, `null` clears, invalid ones are 400 `INVALID_VALUE`). MCP `jobs_list` takes `tags` and shows them. Same on the
  Node and Python servers.
- **Commands as rich as web commands.** `ICliCommandProcessor` gains `Aliases`, `Examples` (`CliCommandExample`),
  `Documentation`, `Module` and `Icon`, and `ICliCommandParameterDescriptor` `AllowedValues` and `Examples` (example values; default interface members,
  virtual properties on `CliCommandProcessor`); `GET /api/v1/qcli/commands` sends them (absent when unset), so the web
  CLI's `help` shows examples and documentation, groups and decorates the command, and completes and checks allowed
  values. The registry resolves aliases of commands and sub-commands for execution, streaming and permission checks
  (a command name wins over another command's alias; `CliCommandNames`). MCP command tools list allowed values as an
  `enum` (and parameter examples as `examples`) and refuse other values. The built-in and demo processors and the weather plugin carry examples and
  documentation (`hash --algorithm` its algorithms). Same on the Node and Python servers (spec: web-cli
  `docs/superpowers/specs/2026-11-12-server-command-metadata-design.md`).
- **Data explorer: per-source permissions, read-only mode, masking, audit and limits**, the same on the Node and Python servers
  (spec: web-cli `docs/superpowers/specs/2026-11-12-data-explorer-hardening-design.md`):
  - **Breaking:** sources are read-only by default. Writes, DDL and several statements are refused before the provider runs
    (`errorCode: "read_only"`), and the SQL providers run queries in a read-only transaction (SQLite `query_only`, PostgreSQL
    `SET TRANSACTION READ ONLY`, MySQL `START TRANSACTION READ ONLY`, SQL Server a transaction that is always rolled back).
    Set `ReadOnly = false` on a source that must write.
    The SQL check reads quotes and comments as MySQL (`--` before a space, `#`, `/*! ... */`, `"..."` strings), PostgreSQL
    (`E'...'`, `$tag$...$tag$`, nested comments), SQL Server (nested comments, `[...]` with `]]`) and SQLite each do, and a query
    must pass under every reading; `COMMIT`, `ROLLBACK` and `DENY` are refused too (SQL Server runs statements without a `;`).
  - New source options: `RequiredPermissions` (403, and the source is hidden from `/sources` and MCP `data_sources`),
    `AllowedObjects` (globs of tables, collections, keys or indices), `MaskColumns` (`***`), `DropColumns`, `Audit`
    (default true: a `data_query` entry per execute in the audit log, details in `Properties`), `RateLimit { MaxQueries, WindowSeconds }` (429)
    and `ExportPermissions`.
  - On a SQL source with `MaskColumns` or `DropColumns`, a query may name those columns only as plain items of the select list
    (not renamed, computed, filtered or sorted on, nor in a `UNION`, nor a whole row), and the result must show them under their own
    names; otherwise it is refused or withheld with `errorCode: "masked_column"`. JSON text in a cell is masked like a document.
  - `AllowedObjects` reads every table of a `FROM` list (after subqueries and functions too), `USING`, `DESCRIBE`, SQL Server `db..table`,
    CTE names only where they are in scope, the collections of `$lookup`, `$graphLookup`, `$unionWith`, `$out` and `$merge`, and refuses
    Elasticsearch bodies that name an index. Elasticsearch `POST` is read-only only after index names (`/logs/_doc/_search` and
    `/logs/_delete_by_query#/_search` are refused), and Redis keys are read as the provider reads them.
  - PostgreSQL sends a read-only query as a single batch command, so it runs one statement at most.
  - `GET /sources` adds `readOnly` and `canExport`; the schema lists only allowed tables without dropped columns; results carry `errorCode` when refused.
  - `IDataExplorerExecutorService.ExecuteAsync(request, caller, ct)` and `IDataExplorerRegistry.GetSources(caller)` (`DataExplorerCaller`,
    built from a request with `DataExplorerCallers.From`); providers that only read can set `ReadOnlyQueries`; the rules are in `DataExplorerPolicy`.
- **Link a terminal through the browser** (users plugin), the same on the Node and Python servers
  (spec: web-cli `docs/superpowers/specs/2026-11-12-server-linking-design.md`): `POST /api/v1/qcli/link` (public,
  rate limited) returns a code and the approval page, `GET /link/{linkId}` hands the terminal its API token once the
  user approved at `/qcli/admin/link?code=…` (`GET /link/code/{code}`, `POST …/approve`, `POST …/deny`). The token is
  the approver's, named `link <client>`, scoped to the requested permissions. `LinkApprovalUrl` points to a host's
  own page; `link_approved` and `link_denied` are audited (`AuditMetadata` gains `Client` and `Origin`).

## 1.0.0-beta.24

- The admin plugin embeds dashboard 1.0.0-beta.19: a phone layout (below 768 px the sidebar is a drawer, stat cards wrap, panels stack and tables scroll in their container) and tests for the live log tail.
- **Admin: live log entries.** `AddAdmin` registers `LogEntryEvents`, a hosted service that publishes every entry the
  log ring buffer captures as `log:entry` (`timestamp`, `level`, `message`, `source`) on `ICliEventBus` with the
  audience `logs:read`, so the dashboard's Logs page follows the log live instead of only after a refresh. A line
  logged while an entry is delivered (the bus about a failing handler, a socket about a failed send) is kept but not
  published. `LogRingBuffer.EntryAdded` is raised after every `Add`.

## 1.0.0-beta.23

- The admin plugin embeds dashboard 1.0.0-beta.18: pages update live from the server events (jobs, job detail, files, audit, overview, events), a run's full error in a details dialog, max concurrent runs in the job editor, and no session token in WebSocket URLs when the ticket request fails.

### Server events

One event bus per server, events for every change, and delivery scoped to who may see them (the same on the Node and
Python servers; spec `web-cli/docs/superpowers/specs/2026-11-06-server-events-design.md`).

- `ICliEventBus` (`Qodalis.Cli.Abstractions.Events`, with `CliEvent`, `CliEventAudience` and `CliEventPattern`),
  registered as a singleton by `AddCli` (`CliEventBus`). `PublishAsync(type, data, audience)` and
  `Subscribe(pattern, handler)` (`*`, `ns:*` or a type). Handlers run in subscription order; their exceptions are
  logged and never fail the publisher. In-process subscribers receive every event.
- The events WebSocket subscribes to the bus and sends one flat JSON object per event (`type`, ISO 8601 `timestamp`,
  the fields) to the sockets of its audience: everyone, a permission, a user, or the user or the permission. Each
  socket keeps its principal; on a server without authentication every socket gets every event. Sends run in the
  background (in order per socket), so a slow client never slows the request that published.
  `ICliEventSocketManager.BroadcastMessageAsync`/`SendToUserAsync` still send raw messages.
- New events: `job:triggered`, `job:updated`, `job:runs-deleted` (jobs API and MCP tools, so a custom
  `ICliJobManager` gets them); `user:*`, `group:*`, `token:*`, `session:*` (users plugin); `fs:changed` (every write
  through `IFileStorageProvider`, now wrapped in `CliEventFileStorageProvider`); `command:executed` (REST, stream and
  MCP executions of known commands: the processor name only); `audit:recorded` (every entry, through
  `CliEventAuditStore`). See the README's "Server events" for fields and audiences.
- Fix: the jobs scheduler sent its events nested (`{ type, data: { jobId } }`), so the dashboard's job pages never
  refreshed live on .NET. They are now flat, sent to `jobs:read` only, `job:paused`/`job:resumed`/`job:stopped` carry
  `status`, `job:completed` carries the run's real `duration`, and the run events are published once the run is
  saved. `CliJobScheduler` takes an `ICliEventBus`; the `ICliEventSocketManager` constructors remain.
- `IAuditStore` and `IFileStorageProvider` resolve to the wrapping `CliEventAuditStore`/`CliEventFileStorageProvider`
  (`Inner`, `CliEventAuditStore.Unwrap`).

### Jobs: max concurrent runs, and no job started twice

Spec `web-cli/docs/superpowers/specs/2026-11-06-job-concurrency-design.md` (the same on the Node and Python servers).

- `CliJobOptions.MaxConcurrentRuns` (default 1, at least 1): up to N runs of a job at the same time. A run holds its
  slot until it ends, including its retries and the delays before them. When the slots are full, `OverlapPolicy`
  decides: `Skip` drops the due run (a trigger is refused, `409 JOB_ALREADY_RUNNING`), `Queue` queues one run (several
  due runs coalesce into one; a trigger is accepted), `Cancel` cancels the oldest run and starts the new one once it
  ended (queued meanwhile, so the limit holds; above a lowered limit it cancels nothing and skips). Lowering the limit cancels nothing; raising it starts a queued run.
- Job info (`GET /jobs`, `GET /jobs/{id}`, MCP `jobs_list`/`jobs_get`, `CliJobInfo`): `maxConcurrentRuns`,
  `runningExecutionIds` (the attempts executing now, oldest first: a run in its retry delay has no entry), `queued`;
  `currentExecutionId` is the newest executing attempt. `PUT /jobs/{id}` accepts `maxConcurrentRuns`, an integer >= 1
  (below 1 is `400 INVALID_VALUE`, a non-integer is `400` too). New `CliJobInfo` members default to 1, empty and false,
  so custom `ICliJobManager` implementations keep compiling.
- `POST /jobs/{id}/trigger` answers `{ "message": "Job triggered", "queued": false }`, or `{ "message": "Job queued",
  "queued": true }` when the run was queued (`Queue`, or `Cancel` while the cancelled run ends); MCP `jobs_trigger`
  says "Job queued". New default member `ICliJobManager.TriggerWithOutcomeAsync` (and
  `ICliJobScheduler.TriggerWithOutcomeAsync`) returns `CliJobTriggerOutcome`; its default calls `TriggerAsync` and
  reports `Started`.
- `cancel` cancels every running run of the job and drops a queued one; `stop` does the same and disables the schedule.
- Fix: `RunOnStart` ran every job on every start. Now an interval job runs on start only when it never ran or its next
  run is due, a cron job only when it never ran or missed an occurrence; a job without a schedule still runs on every
  start.
- Fix: between a failed attempt and its retry the job counted as idle, so a tick or a trigger started a second run.
- Fix: `OverlapPolicy.Queue` started the new run at once, in parallel; it now waits for a free slot.
- Fix: timer ticks and triggers checked "is it running" without a lock, so two at the same moment both started a run.
  Taking a slot is atomic per job.
- Fix: a cron tick that fired a few milliseconds early ran the same occurrence again; the next occurrence is computed
  from the one that fired. A stale timer callback (after an update, pause or stop replaced the timer) does nothing.
- Fix: runs cut off by a restart stayed `running` forever. On start the scheduler finishes its jobs' leftover running
  executions as `cancelled` (`Interrupted: the server stopped during the run`, completed at their last log entry when
  that is later than their start, else at their start).
- Fix: a run cancelled by hand (cancel, stop, the `Cancel` overlap policy) of a job with a `Timeout` was recorded as
  `timedout` and published `job:timed_out`. The timeout has its own cancellation source now: a cancel by hand is
  `cancelled` / `job:cancelled`, only a real timeout is `timedout` / `job:timed_out`.
- `CliJobScheduler` takes an optional `TimeProvider` (from DI when registered) for its clock, timers and retry delays.
  `JobRegistration` gains `RunningExecutionIds` and `Queued`; its `Timer` is an `ITimer`.

## 1.0.0-beta.22

- The admin plugin embeds dashboard 1.0.0-beta.17: logs jump to newest, pause, copy an entry and collapsed exceptions; jobs run again and delete runs from the recent runs table.

## 1.0.0-beta.21

- The admin plugin embeds dashboard 1.0.0-beta.16: delete job runs (one, a result's or every finished one), older log
  entries load when scrolling up with stable paging (Seq and in-memory), and themed dropdown arrows.
- Jobs: delete runs. `DELETE /api/v1/qcli/jobs/{id}/history/{execId}` deletes a finished run and its logs (404
  `EXECUTION_NOT_FOUND`, 409 `EXECUTION_RUNNING`); `DELETE /api/v1/qcli/jobs/{id}/history?status=failed` deletes the
  job's finished runs, or those with a status, and answers `{ deleted }`. Running runs are never deleted. The same on
  the Node and Python servers, with the MCP tool `jobs_delete_runs` and the job action `delete-runs`.
- `ICliJobManager.DeleteExecutionAsync` (default 501) and `DeleteExecutionsAsync` (default: pages through the history
  and deletes each finished run). The scheduler deletes through `ICliJobStorageProvider.DeleteExecutionAsync`.

## 1.0.0-beta.20

- Jobs EF Core: the start-up check for the run table's columns reads `Take(1)`, so EF no longer logs
  `FirstWithoutOrderByAndFilterWarning` on every start.

## 1.0.0-beta.19

- The admin plugin embeds dashboard 1.0.0-beta.15: charts on the Overview, Jobs, Logs, Audit and Commands pages, filters kept in the URL, sortable tables and a Ctrl+K command palette, using the endpoints below.

### Dashboard insights

Read-only endpoints for the admin dashboard's charts, the same JSON on the .NET, Node and Python servers. Times are
ISO 8601 UTC strings, durations milliseconds; a range `[since, until)` (default the last 24 hours) is cut into
`buckets` equal columns (default 24, clamped to 1..200); bad times or counts answer 400 with `{ error }`. Older
dashboards ignore them.

- **Jobs** (`GET /api/v1/qcli/jobs/stats?since=&until=&buckets=&job=&group=`, `jobs:read`): outcomes per column
  (succeeded, failed, cancelled, running), totals and per-job figures (success rate, mean, p95 and max duration, last
  failure, the 12 newest runs) of every run started in the range. Reads the runs through
  `ICliJobManager.QueryExecutionsAsync` a page at a time, so it works with the in-memory store, EF Core and any job
  manager of your own.
- **Logs** (`GET /api/v1/qcli/logs/histogram?from=&to=&buckets=&level=&search=&service=`, admin plugin): entries per
  level in each column, with the filters of `GET /logs`. `ILogStore.HistogramAsync` (default: pages through
  `QueryAsync`, so other stores keep compiling); the in-memory buffer counts itself and the Seq store asks Seq to count
  (`/api/data`), answering 502 when Seq cannot be reached.
- **Status history** (`GET /api/v1/qcli/status/history?minutes=`, admin plugin): a hosted service samples memory, CPU
  (% of one core over the interval), WebSocket clients and shell sessions every 15 seconds, the first at start, into a
  24-hour ring buffer in memory; `minutes` defaults to 60 (1..1440).
- **Command usage** (`GET /api/v1/qcli/commands/stats`): executions, failures (exit code other than 0 or an
  exception), mean duration and last times per root command since start, counted for `/execute`, `/execute/stream`
  and the MCP command tools (`CliCommandUsage`); unknown commands are counted with `known: false`.
- **Audit**: `GET /api/v1/qcli/audit` takes `from` and `to` (ISO 8601 or epoch milliseconds; `from <= timestamp < to`),
  `AuditQueryOptions` gains `From`/`To`, and the EF Core store filters in the query.
- `Qodalis.Cli.Abstractions.Insights.TimeBuckets`: the range, columns, time parsing and nearest-rank percentile the
  endpoints share.

## 1.0.0-beta.18

### Logs

- New plugin **Qodalis.Cli.Plugin.Logs.Seq** (`cli.AddSeqLogs(...)`): sends the logs to Seq, each entry tagged with
  its service (`Service`), and makes Seq the dashboard's log history, so logs survive restarts and an API and its
  workers share one history. Logging never waits for or fails because of Seq. See [its README](plugins/logs-seq/README.md).
- The log history is an `ILogStore` (`Qodalis.Cli.Abstractions.Logs`); the admin plugin's in-memory buffer stays the
  default.
- `GET /api/v1/qcli/logs` takes several levels (`level=WARN,ERROR`), `service`, `from` and `to`, answers 400 with
  `{ error }` for an unknown level or time, 502 when the store cannot be reached, and reports `hasMore`, `store`,
  `persistent` and `services`. `total` is null for stores that cannot count (Seq).
- The admin plugin ships dashboard 1.0.0-beta.14: the Logs page filters on the server and, with Seq, by service and
  time.
- The demo server sends its logs to Seq when `Seq:ServerUrl` is set.

## 1.0.0-beta.17

- The users plugin's hosted service no longer throws `ObjectDisposedException` when it is stopped after being
  disposed. With minimal hosting the application's own `Run()` can dispose the container while
  `WebApplicationFactory` is still stopping the host, which failed test classes at cleanup.

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
