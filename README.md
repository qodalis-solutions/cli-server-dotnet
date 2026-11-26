# Qodalis CLI Server (.NET)

ASP.NET Core server for the [Qodalis CLI](https://cli.qodalis.com/docs): run server-side commands, files, data sources, jobs and an interactive shell from a web terminal.

[![NuGet](https://img.shields.io/nuget/v/Qodalis.Cli?label=Qodalis.Cli)](https://www.nuget.org/packages/Qodalis.Cli)
[![NuGet](https://img.shields.io/nuget/v/Qodalis.Cli.Abstractions?label=Qodalis.Cli.Abstractions)](https://www.nuget.org/packages/Qodalis.Cli.Abstractions)
[![Build & Test](https://github.com/qodalis-solutions/cli-server-dotnet/actions/workflows/build.yml/badge.svg)](https://github.com/qodalis-solutions/cli-server-dotnet/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

## Overview

You write command processors in C#, register them with `AddCli()`, and the Qodalis web terminal discovers and runs them over REST and WebSocket. Optional plugins add a file system API, data explorer providers (SQL, MongoDB, Redis, ...), scheduled jobs, users and API tokens, an admin dashboard and an MCP endpoint for AI clients.

It is one of three interchangeable servers that expose the same API:

| Repository | Role |
|---|---|
| [web-cli](https://github.com/qodalis-solutions/web-cli) | Browser terminal (Angular, React, Vue, vanilla) and 40 client plugins |
| **cli-server-dotnet** | This server (.NET / ASP.NET Core) |
| [cli-server-node](https://github.com/qodalis-solutions/cli-server-node) | Node.js / Express server |
| [cli-server-python](https://github.com/qodalis-solutions/cli-server-python) | Python / FastAPI server |
| [cli-server-dashboard](https://github.com/qodalis-solutions/cli-server-dashboard) | Admin dashboard served by the admin plugin at `/qcli/admin` |
| [mcp-cli](https://github.com/qodalis-solutions/mcp-cli) | `@qodalis/mcp-cli`, a stdio MCP bridge for Claude Code and other MCP clients |

Requires **.NET 10**.

## Quick start

```bash
dotnet new web -n MyCliServer && cd MyCliServer
dotnet add package Qodalis.Cli
```

`Program.cs`:

```csharp
using Qodalis.Cli;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        // CORS and WebSocket Origin allow-list (the library configures no CORS otherwise).
        cli.SetAllowedOrigins("http://localhost:4200");
        cli.AddProcessor<GreetCommandProcessor>();
    });

var app = builder.Build();

app.UseWebSockets();
app.UseCors();
app.MapControllers();
app.UseCli();

app.Run();

public class GreetCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "greet";
    public override string Description { get; set; } = "Greets the user";

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        => Task.FromResult($"Hello, {command.Value ?? "World"}!");
}
```

Run it with `dotnet run`, then point a web-cli terminal at the server URL and type `greet Alice`. See the [web-cli docs](https://cli.qodalis.com/docs) for the client side.

Optionally serialize responses in camelCase and close open sockets cleanly on shutdown, as the bundled host does:

```csharp
builder.Services.AddControllers().AddCli(/* ... */).AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

// after app.UseCli():
var events = app.Services.GetRequiredService<Qodalis.Cli.Services.ICliEventSocketManager>();
var logs = app.Services.GetRequiredService<Qodalis.Cli.Services.ICliLogSocketManager>();
app.Lifetime.ApplicationStopping.Register(() =>
{
    events.BroadcastDisconnectAsync().GetAwaiter().GetResult();
    logs.BroadcastDisconnectAsync().GetAwaiter().GetResult();
});
```

## Features

- **Command processors** with typed parameters, aliases, defaults, sub-commands, permissions and streaming output (`ICliStreamCommandProcessor`).
- **Modules** that bundle processors (`ICliModule` / `CliModule`), plus assembly scanning.
- **Dependency injection**: processors registered by type are resolved from the ASP.NET Core container.
- **WebSockets** for server events, log streaming and an opt-in, authenticated PTY shell.
- **Plugins**: file system (memory, OS, JSON, SQLite, EF Core, S3), data explorer (SQLite, PostgreSQL, MySQL, SQL Server, MongoDB, Redis, Elasticsearch), jobs, users, admin dashboard, AWS.
- **MCP endpoint** that exposes commands and plugin features as tools to AI clients.
- **Config providers**: the server's settings, read and changed at runtime from the dashboard, the `config` command and MCP tools, each provider in its own store or the default one: memory, a JSON file, a section of appsettings.json, a database or Redis.
- **Version discovery** so clients negotiate the API version.

## Packages

| Package | Purpose |
|---|---|
| [`Qodalis.Cli.Abstractions`](https://www.nuget.org/packages/Qodalis.Cli.Abstractions) | Interfaces for plugin authors (`ICliCommandProcessor`, parameters, modules, data explorer, jobs) |
| [`Qodalis.Cli`](https://www.nuget.org/packages/Qodalis.Cli) | ASP.NET Core integration: controllers, `CliBuilder`, WebSockets, MCP, base classes |

Plugins are published separately; see [Plugins](#plugins).

## Writing commands

### Parameters

```csharp
public class TimeCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "time";
    public override string Description { get; set; } = "Shows the current server time";

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "utc",
            Description = "Show time in UTC",
            Type = CommandParameterType.Boolean,
        },
        new CliCommandParameterDescriptor
        {
            Name = "format",
            Aliases = ["-f"],
            Description = "Date/time format string",
            Type = CommandParameterType.String,
            DefaultValue = "yyyy-MM-dd HH:mm:ss",
        },
    ];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var utc = command.Args.ContainsKey("utc");
        var format = command.Args.TryGetValue("format", out var f) ? f.ToString()! : "yyyy-MM-dd HH:mm:ss";
        var now = utc ? DateTime.UtcNow : DateTime.Now;
        return Task.FromResult($"{(utc ? "UTC" : "Local")}: {now.ToString(format)}");
    }
}
```

Parameter types (`CommandParameterType`): `String`, `Number`, `Boolean`, `Array`, `Object`.

### Sub-commands

Nest processors through `Processors` to build hierarchies such as `math add --a 5 --b 3`:

```csharp
public class MathCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "math";
    public override string Description { get; set; } = "Performs basic math operations";
    public override bool? AllowUnlistedCommands { get; set; } = false;
    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; } =
        [new MathAddProcessor(), new MathMultiplyProcessor()];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        => Task.FromResult("Usage: math add|multiply --a <number> --b <number>");
}
```

### Processor members

| Member | Type | Default | Description |
|---|---|---|---|
| `Command` | `string` | required | Command name |
| `Description` | `string` | required | Help text |
| `HandleAsync` | method | required | Execution logic, returns the output text |
| `Parameters` | `IEnumerable<ICliCommandParameterDescriptor>?` | `null` | Declared parameters |
| `Processors` | `IEnumerable<ICliCommandProcessor>?` | `null` | Sub-commands |
| `AllowUnlistedCommands` | `bool?` | `null` | Accept sub-commands not in `Processors` |
| `ValueRequired` | `bool?` | `null` | Require a positional value |
| `AcceptsRawInput` | `bool?` | `null` | Take an optional positional input |
| `RequiredPermissions` | `IEnumerable<string>?` | `null` | Permissions checked on execute (users plugin) |
| `Aliases` | `IEnumerable<string>?` | `null` | Other names; the server resolves them (and their permissions) like the command |
| `Examples` | `IEnumerable<CliCommandExample>?` | `null` | Example lines (`new("time --utc", "UTC time")`) shown by `help <command>` in the web CLI |
| `Documentation` | `string?` | `null` | Longer help text for `help <command>` (paragraphs separated by a blank line) |
| `Module`, `Icon` | `string?` | `server:<name>`, 🖥 | The group and emoji the web CLI's `help` lists the command under |

A parameter's `AllowedValues` lists the only values it takes: the web CLI shows them in help, completes them with Tab
and refuses others before sending; MCP tools list them as an `enum`. `Examples` lists example values, shown in help and
as `examples` in MCP. Every member above is a default interface member,
so processors that implement `ICliCommandProcessor` directly keep compiling.
| `Version` | `string` | `"1.0.0"` | Processor version |
| `Author` | `ICliCommandAuthor` | library author | Author metadata |

### Command input

`CliProcessCommand` carries the parsed input:

| Property | Description |
|---|---|
| `Command` | Command name (`time`) |
| `Value` | Positional argument (`hello` in `echo hello`) |
| `Args` | Named parameters (`--format "HH:mm"`) |
| `ChainCommands` | Sub-command chain (`["add"]` in `math add`) |
| `RawCommand` | Original input |
| `Data` | Arbitrary payload from the client |

### Paging

A command that lists records pages them, so a terminal never gets thousands of rows. Declare `--skip` / `--take`, read them and write the rows with `WritePagedTable`; the web terminal prints `Rows 51–100 of 236 · next: orders --skip 100` under the table.

```csharp
using Qodalis.Cli.Abstractions.Helpers;

public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = CliPaging.Parameters();

public async Task<ICliStructuredResponse?> HandleStructuredAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
{
    var page = CliPaging.From(command);          // --skip (default 0), --take (default 50, at most 500)
    var total = await db.Orders.CountAsync(cancellationToken);
    var rows = await db.Orders.OrderBy(o => o.Id).Skip(page.Skip).Take(page.Take)
        .Select(o => new[] { o.Id.ToString(), o.Customer }).ToArrayAsync(cancellationToken);

    var builder = new CliResponseBuilder();
    builder.WritePagedTable(["Id", "Customer"], rows, page, total);   // or hasMore: when the total is costly
    return builder.Build();
}
```

Tables from commands that do not page are cut at `CliServerOptions.MaxTableRows` (default 200, `0` = no limit) and the terminal says rows were left out.

### Registration

```csharp
builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddProcessor<GreetCommandProcessor>();                // by type, resolved from DI (singleton)
    cli.AddProcessor(new TimeCommandProcessor());             // pre-built instance
    cli.AddProcessorsFromAssembly(typeof(Program).Assembly);  // every ICliCommandProcessor in the assembly
    cli.AddModule(new WeatherModule());                       // a module's processors
});
```

Core has one built-in command, `http` (`CliHttpCommandProcessor` in `Qodalis.Cli.Processors`: `http get|post <url>` sent from the server, module `core`), which needs the `http:request` permission; hosts opt in with `cli.AddProcessor<CliHttpCommandProcessor>()`. The demo's example commands (`echo`, `status`, `system`, `hash`, `base64`, ...) live in `demo/Processors`, not in the packages.

Processors are singletons. To use scoped services such as a `DbContext`, inject `IServiceScopeFactory` and create a scope inside `HandleAsync`.

### Modules

```csharp
public class WeatherModule : CliModule
{
    public override string Name => "weather";
    public override string Version => "1.0.0";
    public override string Description => "Provides weather information commands";
    public override IEnumerable<ICliCommandProcessor> Processors { get; } = [new CliWeatherCommandProcessor()];
}
```

`plugins/weather/` is a complete reference module. A module can bring the permissions its commands require to the
[permission catalog](#permission-catalog): `Permissions` (`CliModulePermission(name, description, group)`), added by
`cli.AddModule` with surface `cli`; a name already in the catalog stays as registered (see `AwsModule`).
`GET /api/v1/qcli/commands` lists a module's commands with the module's name as `module` (unless a command names one
itself), so the dashboard's Plugins page groups them under the module.

Each command, and each sub-command in `processors`, lists the permissions it declares itself (`RequiredPermissions`)
as `requiredPermissions`; the field is left out when it declares none. Running a command also needs the permissions of
the commands above it and, on a protected server, `commands:execute`; neither is repeated:

```json
{
  "command": "aws",
  "module": "aws",
  "requiredPermissions": ["aws:read"],
  "processors": [
    {
      "command": "s3",
      "processors": [
        { "command": "ls" },
        { "command": "rm", "requiredPermissions": ["aws:write"] }
      ]
    }
  ]
}
```

## Configuration

All options are set on the `CliBuilder` passed to `AddCli(cli => ...)`.

| Method | Description |
|---|---|
| `SetAllowedOrigins(params string[])` | CORS and WebSocket Origin allow-list; `http://127.0.0.1:*` matches any port. Same-origin requests always pass. |
| `EnableShell()` / `EnableShell(Action<CliShellOptions>)` | Enables the interactive shell sockets (off by default) |
| `AddMcp(Action<McpOptions>?)` | Enables `POST /api/qcli/mcp` |
| `AddFileSystem(Action<FileSystemOptions>?)` | Enables `/api/qcli/fs/*` |
| `AddDataExplorerProvider<T>(...)` / `AddDataExplorerProvider(instance, ...)` | Registers a data explorer source (read-only by default, see [Data explorer sources](#data-explorer-sources)) |
| `AddProcessor`, `AddProcessorsFromAssembly`, `AddModule` | Registers commands |
| `AddApplicationPart(Assembly)` | Adds a plugin assembly's controllers |
| `AddPermissions(Action<CliPermissionCatalogBuilder>, surface = "host")` | Adds entries to the permission catalog users pick from (see [Permission catalog](#permission-catalog)) |
| `Services` | The `IServiceCollection`, for plugin registrations |
| `ServerOptions.MaxTableRows` | Rows an unpaged table may return before it is cut (default 200, `0` = no limit); see [Paging](#paging) |
| `AddConfigProvider<T>()`, `AddConfigProvider(instance)`, `AddConfigProvider(sp => ...)` | Registers a config provider (see [Config providers](#config-providers)) |
| `SetConfigStore(store)`, `ConfigureConfig(o => ...)` | Where writable settings are kept; the `config` command and what the `host` provider lists |
| `WithoutCliRoutes()` | Leaves out every CLI route and socket (`/api/qcli/*`, `/api/v1/qcli/*`, `/api/v2/qcli/*`, `/api/cli/*`, `/ws/*`): no controllers of core or plugins (also those MVC discovers on its own: a Web SDK host lists every referenced `Qodalis.Cli*` assembly as an application part, and `AddCli` removes their controllers; the host's own controllers stay), `UseCli()` throws. For a service that only uses the guards and services, e.g. with the users plugin's `UseGuardOnly()`; the permission catalog, events and services stay |

### Permission catalog

The server keeps a catalog of the permissions users can choose when they grant them (the dashboard's and the console's pickers, `GET /api/v1/qcli/permissions` of the users plugin). Each entry is `{ name, description, group, surface }`:

```json
{ "name": "files:read", "description": "List, read and download files", "group": "Files", "surface": "cli" }
```

Core registers the permissions its route policy checks and its `http` command needs (`commands:execute`, `shell:execute`, `http:request`, `files:read`, `files:write`, `data:query`, `jobs:read`, `jobs:manage`, `logs:read`, `admin:access`, `config:read`, `config:write`, `audit:read`, `audit:clear`); the users plugin adds its 12 (`users:*`, `groups:*`, `sessions:*`, `tokens:*`). Your app adds the permissions its own routes check:

```csharp
cli.AddPermissions(p => p.Group("Rates")
    .Add("rates:read", "Official exchange rates")
    .Add("banks:read", "Banks and their buy and sell rates"));
```

- Your entries get the `host` surface: they are the only permissions a `host` API token can carry (see [host tokens](plugins/users/README.md#host-tokens-iapitokenmanager)). Plugins register theirs with `AddPermissions(configure, CliPermissionSurface.Cli)`.
- `ICliPermissionCatalog.All` lists the `cli` entries (core, then plugins, in registration order), then the `host` entries, whatever order the calls ran in. `Find(name)` is case-insensitive.
- A name is lower case with one `:` and no `*` (`^[a-z0-9_.-]+:[a-z0-9_.-]+$`); a description is 1-200 characters on one line, a group 1-64 characters. An invalid entry fails startup with `invalid permission entry: <name>`, a name already registered (case-insensitive, also one a plugin owns, in either call order) with `permission already registered: <name>`.
- Registration is startup only: `AddPermissions` after the catalog is first resolved throws.
- The catalog is for choosing, not for validating: groups and `cli` tokens still accept any permission, including wildcards and commands' own `RequiredPermissions`.

### Shell (`CliShellOptions`)

The shell gives OS-level access, so it is disabled unless enabled, and enabling it does not open it: exactly one access mode decides who may connect.

| Mode | When | Client sends |
|---|---|---|
| `users` | the users plugin is installed | `?ticket=` of a session holding `shell:execute` |
| `token` | `Token` is set (no users plugin) | `Authorization: Bearer <token>` (browsers: `?_header_Authorization=Bearer%20<token>`) |
| `custom` | `Authorize` is set (no users plugin) | whatever the callback checks |
| `anonymous` | only with `AllowAnonymous = true` | nothing; logs a startup warning |
| `locked` | enabled, none of the above | every connection closed with `4401` |

```csharp
cli.EnableShell(o => o.Token = builder.Configuration["Shell:Token"]); // long random secret
```

| Option | Default | Description |
|---|---|---|
| `Token` | none | Bearer token (constant-time comparison, never logged) |
| `Authorize` | none | `Func<HttpContext, ValueTask<bool>>` access check |
| `AllowAnonymous` | `false` | Ignored once `Token` or `Authorize` is set |
| `MaxSessionsPerUser` | `3` | Per user or remote address (close `4429`) |
| `MaxSessions` | `20` | Total concurrent shells |
| `IdleTimeout` | 15 min | Without input (close `4408`); `null` = never |
| `MaxDuration` | none | Total lifetime |
| `SessionCheckInterval` | 30 s | Users-mode re-validation (capped at 60 s) |
| `WorkingDirectory` | server user's home | |
| `Shell` | detected (`$SHELL`, bash, sh; PowerShell on Windows) | |
| `EnvironmentAllow` / `EnvironmentSet` | empty | Extra variables passed through / set |

The child process gets a minimal environment (`PATH`, `HOME`, `USER`, `LANG`, `TERM`, `COLUMNS`, `LINES`, `SHELL` plus the lists above), never the full server environment. When a shell ends, its whole process tree is terminated, including detached and double-forked children (tracked by a per-shell `QCLI_SHELL_ID` environment marker). In users mode an open shell closes when its session ends or loses `shell:execute`, and the audit log records `shell_opened` / `shell_closed`. `GET /api/v1/qcli/capabilities` reports `shell` and `shellAuth`.

Rejected connections are accepted and then closed with a readable code: `4401` (login required, session ended, not configured), `4403` (forbidden), `4404` (disabled), `4408` (timed out), `4429` (too many sessions). A disallowed `Origin` gets HTTP 403.

### MCP (`McpOptions`)

```csharp
cli.AddMcp(o =>
{
    o.Tools = ["weather_*", "fs_*", "server_info"]; // optional allow-list, trailing * = prefix
    o.Exclude = ["fs_delete"];                       // optional deny-list, applied after Tools
});
```

| Option | Default | Description |
|---|---|---|
| `Enabled` | `true` | Turn the endpoint off without removing the configuration |
| `Tools` | all | Allow-list of tool names |
| `Exclude` | none | Deny-list, applied after `Tools` |
| `Token` | none | Bearer token required when the server has **no** authentication provider |
| `Instructions` | none | Returned by `initialize` |

The endpoint is MCP Streamable HTTP, stateless, with JSON responses (`initialize`, `ping`, `tools/list`, `tools/call`). Tools are:

| Source | Tools | Permission |
|---|---|---|
| server | `server_info` | none |
| commands | one per leaf command, value-taking or parameterized processor (`weather_current`, `math_add`, ...) | `commands:execute` + `RequiredPermissions` |
| file system | `fs_list`, `fs_stat`, `fs_read`, `fs_write`, `fs_mkdir`, `fs_delete` | `files:read` / `files:write` |
| data explorer | `data_sources`, `data_schema`, `data_query` | `data:query` + the source's `RequiredPermissions` |
| jobs plugin | `jobs_list`, `jobs_get`, `jobs_history`, `jobs_trigger`, `jobs_pause`, `jobs_resume`, `jobs_stop`, `jobs_cancel`, `jobs_delete_runs` | `jobs:read` / `jobs:manage` |

With the users plugin, requests authenticate with a session or an API token (`Authorization: Bearer qcli_pat_...`) and `tools/list` only lists what the caller may call. Plugins add tools by implementing `IMcpToolProvider` and calling `services.AddMcpToolProvider<T>()`.

Connect Claude Code directly, or through the [`@qodalis/mcp-cli`](https://github.com/qodalis-solutions/mcp-cli) bridge:

```bash
claude mcp add --transport http qcli http://localhost:8046/api/qcli/mcp --header "Authorization: Bearer <token>"
claude mcp add qcli -e QCLI_URL=http://localhost:8046 -e QCLI_TOKEN=<token> -- npx -y @qodalis/mcp-cli
```

### Security

Without an authentication plugin the server is open. Install the users plugin (`cli.AddUsers()` and `app.UseQodalisUsers()` before `app.UseCli()`); with its default `ProtectServer = true` every CLI route and socket requires a session, except sign-in and version discovery. `host` API tokens (created by your app for its own routes) are refused on every CLI route and socket, protected or not. See [plugins/users/README.md](plugins/users/README.md).

## Config providers

The server's configuration is read and changed through **config providers** that the server, its plugins and the host register: the same contract and JSON as the Node and Python servers. The admin plugin serves them over HTTP (the dashboard's Settings page), the `config` command in the terminal and the `config_*` tools over MCP.

```csharp
using Qodalis.Cli.Abstractions.Config;

cli.AddConfigProvider(new CliStoredConfigProvider(new CliStoredConfigProviderOptions
{
    Id = "billing", Title = "Billing",
    Sections =
    [
        new()
        {
            Id = "invoices",
            Settings =
            [
                new() { Key = "dueDays", Type = CliConfigEntryTypes.Number, Default = 30,
                        Validation = new() { Min = 1, Max = 90 }, Apply = v => billing.DueDays = (int)v! },
                new() { Key = "apiKey", Type = CliConfigEntryTypes.Secret },
            ],
        },
    ],
}));
cli.SetConfigStore(new JsonFileConfigStore("config.json")); // the default store (default: in memory)
cli.SetAppSettingsConfigStore("billing");                   // billing in appsettings.json, section Qcli:Config
```

- **Contract** (`Qodalis.Cli.Abstractions.Config`): `ICliConfigProvider` (`Id`, `Title`, `Order`, `RequiredPermission`, `GetSectionsAsync`) and, for writable providers, `ICliConfigWriter` (`ValidateAsync`, `UpdateAsync`). Entries are typed `string`, `number`, `boolean`, `string[]`, `enum`, `secret`, `json`, `duration` (`500ms`, `30s`, `15m`, `12h`, `7d`) or `url`, with a source (`default`, `file`, `environment`, `database`, `runtime`), `mutable`, `requiresRestart`, `sensitive`, validation (`min`, `max`, `pattern`, `options`, `required`) and an `editor` hint. Each writable section has a version: a change made against an older one gets 409.
- **`CliStoredConfigProvider`** describes settings in code and keeps changes in its config store; `Apply` makes the effective value take effect at startup and after every change. `CliCustomConfigProvider.Create()` is the free key/value `custom` provider.
- **Stores** (a store keeps only the values changed from their defaults, with a version per section, keyed by provider and section: one store can serve several providers):

| Store | Source | Where |
|---|---|---|
| `InMemoryConfigStore` (default) | `runtime` | memory: changes apply at once and are lost on restart |
| `JsonFileConfigStore(path)` | `file` | one JSON file, rewritten on every change (temp file + rename, mode 0600) |
| `JsonFileConfigStore(path, new() { Section = "qcli" })` | `file` | a section of a JSON settings file: `qcli`, or nested as `app.qcli` / `App:Qcli`. The file is re-read on every write and only the section replaced: the other keys, their order, the indentation, line endings and the file's mode stay (comments are read but not kept) |
| `AppSettingsConfigStore` (`cli.SetAppSettingsConfigStore(id?)`) | `file` | the app's `appsettings.json` (or `appsettings.{Environment}.json`, `o.Environment = "Development"`) in the content root, section `Qcli:Config`, each section with its version: `Qcli:Config:<provider>:<section>:values:<key>`. A host reading the file with `reloadOnChange` (ASP.NET Core's default) sees every change in its `IConfiguration` |
| `EfCoreConfigStore` ([`Qodalis.Cli.Plugin.Config.EfCore`](plugins/config-efcore/README.md), `cli.AddEfCoreConfigStore(o => o.UseNpgsql(...), providerId: "jobs")`) | `database` | table `QcliConfig`, one row per section, the version a concurrency token |
| `RedisConfigStore` ([`Qodalis.Cli.Plugin.Config.Redis`](plugins/config-redis/README.md), `cli.AddRedisConfigStore(o => o.Url = "redis://cache:6379/0", providerId: "jobs")`) | `database` | one Redis hash per section (`qcli:config:<provider>:<section>`), writes checked against the version by a Lua script |

- **Each provider can have its own store** (some in `appsettings.json`, some in a database, some in Redis). The store a provider gets, first match wins:
  1. the host's store for its id: `cli.SetConfigStore(store, "jobs")`, `cli.SetConfigStore<TStore>("jobs")` (resolved from the container, one instance for every id and the default given that type), `cli.SetConfigStore(sp => ..., "jobs")`, `cli.ConfigureConfig(o => o.Stores["jobs"] = store)` (`SetConfigStore` wins for the same id), `AddEfCoreConfigStore(..., providerId)`, `AddRedisConfigStore(..., providerId)`, `SetAppSettingsConfigStore(id)`;
  2. the provider's own: `new CliStoredConfigProviderOptions { Store = store }`, `provider.SetStore(store)` before it is registered, `CliCustomConfigProvider.Create(store: ...)`, or the plugin that adds it: `ConfigureJobs(o => o.ConfigStore = ...)` (`jobs`), `AddUsers(o => o.ConfigStore = ...)` (`users`), `AddPortal(o => o.ConfigStore = ...)` (`portal`), `AddAdmin(o => o.LogsConfigStore = ...)` (`logs`);
  3. the default store: `cli.SetConfigStore(store)`, `SetConfigStore<TStore>()`, `SetConfigStore(sp => ...)`, `AddEfCoreConfigStore(...)` / `AddRedisConfigStore(...)` without an id, `SetAppSettingsConfigStore()`;
  4. in memory.

  A store for an id can also be set at runtime with `CliConfigRegistry.SetStore(store, id)`, until that provider is registered; afterwards it throws. Stored values are applied while the server starts; `CliConfigService.ReadyAsync()` waits for every store (`ICliConfigStore.ReadyAsync`, optional: e.g. the Redis store connects) and every provider's values. Versions and conflicts are per store and section. Implement `ICliConfigStore` (`ReadAsync`, `WriteAsync`, optional `Describe()` and `ReadyAsync`) to keep them elsewhere. Secrets are stored as given: keep the file, table or keys readable by the server only.
- **Where changes are saved**: every store says where it keeps the settings with `Describe()` (`{ kind: memory|file|database|redis|custom, title, detail, persistent }`), shown as each stored provider's `store` and as `defaultStore` by `GET /config`, on the dashboard's Config page and by `config list`. `detail` names the file (relative to the working directory, or its name only outside it) and its section, the provider, database name and table (`SQL Server database qcli, table QcliConfig`), or the Redis host, port, database and key prefix, never credentials or a connection string. A store returning null from `Describe()` is a `custom` store named after its class, persistent unless its source is `runtime` (`CliConfigStoreHelpers.Describe`, `DescribeName`, `DescribeLine`). Every change is audited with the kind of store it was written to (`properties.store` of `config.update`) and published with it (`store` of `config:changed`); a provider that saves itself has neither.

| Store | `Describe()` | `config list` |
|---|---|---|
| `InMemoryConfigStore` | `{ "kind": "memory", "title": "In memory", "detail": "", "persistent": false }` | `In memory` |
| `JsonFileConfigStore` | `{ "kind": "file", "title": "JSON file", "detail": "config.json", "persistent": true }` | `JSON file (config.json)` |
| `JsonFileConfigStore` with `Section` | `{ "kind": "file", "title": "JSON file", "detail": "config/app.json, section qcli", "persistent": true }` | `JSON file (config/app.json, section qcli)` |
| `AppSettingsConfigStore` | `{ "kind": "file", "title": "appsettings.json", "detail": "section Qcli:Config", "persistent": true }` | `appsettings.json (section Qcli:Config)` |
| `EfCoreConfigStore` | `{ "kind": "database", "title": "Database", "detail": "PostgreSQL database qcli, table QcliConfig", "persistent": true }` | `Database (PostgreSQL database qcli, table QcliConfig)` |
| `RedisConfigStore` | `{ "kind": "redis", "title": "Redis", "detail": "cache:6379/0, prefix qcli:config", "persistent": true }` | `Redis (cache:6379/0, prefix qcli:config)` |

`config list` starts with the default store and ends each stored provider's line with its store:

```text
Default store: JSON file (qcli-demo-config.json), kept across restarts
server  Server  (read-only)
cors  CORS  · JSON file (qcli-demo-config.json)
jobs  Jobs  · Database (SQLite database qcli-demo.sqlite, table QcliConfig)
demo  Demo app  · appsettings.json (section Qcli:Config)
custom  Custom  · Redis (cache:6379/0, prefix qcli:config)
```

- **Where settings come from**: every provider view has `storage` (`{ kind, description, sources }`; a provider sets it with its optional `Storage` property), then, for a stored provider, `store` (fields in this order: `id, title, description, order, writable, storage, store?, sections`): `store` for every `CliStoredConfigProvider` (`cors`, `mcp`, `jobs`, `logs`, `users`, `portal`, `custom`, ...), `runtime` for `server`, `options` for `auth`, `configuration` for `host` with its sources in the order they apply, file names only (`Server options`, `appsettings.json`, `appsettings.Development.json`, `User secrets`, `Environment variables`, `Command line`, from `IConfigurationRoot.Providers`), and `custom` (`Saved by the provider` or `Read by the provider`) for a provider without it. `config list` shows a read-only provider's sources: `host  Host  (read-only; from Server options, appsettings.json, Environment variables)`.
- **Modules** bring their own providers with `ICliModule.ConfigProviders`.
- **Built-in providers**: `server` (the process, read-only), `host` (the server options, the allowed environment variables and `IConfiguration` keys, secrets masked; `cli.ConfigureConfig(o => o.Environment.Allow.Add("APP_*"))`), `cors` (allowed origins at runtime), `mcp` (tool lists, instructions, read-only mode), `filesystem` (storage and roots, read-only), `custom`; from plugins `auth` and `logs` (admin), `jobs` (history retention) and `users` (sessions, lockout, passwords). A host provider with a built-in id replaces the built-in.
- **Secrets** are never returned: `value: null` and `isSet`. `null` resets a setting to its default (clears a secret, removes a free key).
- **Permissions** `config:read` and `config:write` (plus a provider's `RequiredPermission`) on a protected server; every change is audited as `config.update` (old and new values, secrets as `***`, `via` api, terminal or mcp) and published as `config:changed` (audience `config:read`, keys only).

| Method | Path | Description |
|---|---|---|
| GET | `/api/v1/qcli/config` | `{ defaultStore, providers: [...] }`: the store providers use unless they have their own, and the providers the caller may see (`config:read`), each stored one with its `store` |
| GET | `/api/v1/qcli/config/{provider}` | One provider, with its `store` (403, 404) |
| PATCH | `/api/v1/qcli/config/{provider}/{section}` | `{ changes, version? }` (`config:write`): 200 with the section, applied keys and new version; 400 `{ error, errors }`; 409 `{ error, version }` |

Terminal: `config list`, `config get <provider>[.<section>[.<key>]]`, `config set <provider>.<section>.<key> <value> [--expected-version v]`, `config reset <provider>.<section>.<key>` (left out with `ConfigureConfig(o => o.Command = false)`). MCP: `config_list` (the default store, providers with their `storage` and `store`, and section summaries), `config_get` (read-only) and `config_set`; `McpOptions.ReadOnly = true` lists and allows only tools annotated read-only.

## Restart

Some changes take effect only after a restart: a setting with `RequiresRestart`, a plugin installed later. The server
keeps the list of them and can restart itself, from the dashboard, `admin restart --yes` (admin plugin) or the API.

```csharp
builder.Services.AddControllers().AddCli(cli =>
{
    // auto (default): exit under a detected supervisor, else not supported
    // exit: stop the host, exit code ExitCode (default 75) for Docker, systemd, PM2, Kubernetes or IIS to start it again
    // self: once the host stopped, start the same command again (same arguments, environment, working directory)
    // handler: your function, once the host stopped
    cli.ConfigureRestart(o => o.Mode = CliServerRestartModes.Self);
});

var restart = app.Services.GetRequiredService<CliServerRestart>();   // or ICliServerRestart
restart.MarkRequired(new("plugin:tickets", "tickets", "Tickets plugin installed"));
restart.ClearRequired("plugin:tickets");
restart.OnShutdown("pool", () => pool.CloseAsync());                 // your own clean-up, in registration order
```

- **Pending list**: `MarkRequired(new(id, source, description))` (once per id), `ClearRequired(id)`; empty after every
  start. The config service marks a changed entry with `RequiresRestart` as `config:<provider>.<section>.<key>` (source
  `config`, `CORS: Mode changed`) and clears it when the value is set back to what it was at start (a secret stays
  marked: its value is never seen).
- **Supervisor detection** (`auto`): PM2 (`pm_id`), systemd (`INVOCATION_ID`), IIS (an `ASPNETCORE_IIS_*` variable, or
  the in-process host `w3wp` / `iisexpress`: IIS starts the app pool again), Kubernetes (`KUBERNETES_SERVICE_HOST`,
  `kubepods` in `/proc/1/cgroup`), Docker (`/.dockerenv`, `docker` in `/proc/1/cgroup`). Without one, restarting is not
  supported until `Mode` is set.
- **Graceful shutdown**: every WebSocket (events, logs, shells) closed with **1012** (service restart; a client that
  never answers the close is left after 5 s), the `OnShutdown` hooks, then the host's stop
  (`IHostApplicationLifetime.StopApplication()` with the host's shutdown timeout set to `ShutdownTimeout`, 10 s):
  Kestrel stops accepting and lets in-flight requests finish, hosted services such as the job scheduler stop, the
  container disposes the plugins' services.
- **`exit`** sets `Environment.ExitCode` (75) and stops the host: `app.Run()` returns and the process exits with it.
- **`self`** starts `Environment.ProcessPath` with the original arguments once the host stopped (the port is free); under
  the `dotnet` host (`dotnet app.dll`, `dotnet run` without an apphost) `dotnet <entry dll> <arguments>`. Same
  environment and working directory, the output where this process writes. Exit code 1 when it cannot be started.
- **`handler`**: `o.Handler = async context => ...` (`context.Reason`, `Pending`, `By`) runs once the host stopped.
- **Sign-in**: on a server without an auth provider protecting it, restarting is refused (403) unless
  `AllowAnonymous` is set.
- **Tests**: `o.Runtime` (`ICliServerRestartRuntime`: the environment and files the detection reads, stopping the host,
  starting a process) replaces what the process does, so tests never stop their host or start processes.

| Route | Permission | Does |
|---|---|---|
| `GET /api/v1/qcli/server/restart` | `admin:access` | The status below |
| `POST /api/v1/qcli/server/restart` `{ reason? }` | `server:restart` | 202 `{ restarting: true, pending: [ids] }`, then the restart; 409 `{ error: 'restart not supported: no supervisor detected; set restart.mode' }` or `restart already in progress`; 403 without sign-in; 400 `reason must be a string`, `reason must be at most 500 characters` |

```json
{
  "supported": true,
  "mode": "self",
  "supervisor": null,
  "startedAt": "2026-11-25T10:00:00.000Z",
  "pending": [
    { "id": "config:cors.cors.mode", "source": "config", "description": "CORS: Mode changed", "since": "2026-11-25T10:04:05.000Z" }
  ]
}
```

`mode` is the mode in effect (`auto` shows `exit`), null when not supported, with `reason`
(`no supervisor detected; set restart.mode`). `startedAt` changes once the server is back: poll it (or
`GET /api/qcli/versions`) to know when.

- **Permission** `server:restart` (core catalog, group `Server`, `CliRoutePolicy.ServerRestart`).
- **Audit** `server.restart`: `target` `server`, `details` the reason, `properties` `{ reason, by, pending, mode, supervisor, via }`.
- **Events** (audience `admin:access`): `server:restart-required` `{ pending }` whenever the list changes,
  `server:restarting` `{ reason, by, pending, mode }` before the shutdown.

## Server plugins from code or the dashboard

Every official plugin except `admin` can be added two ways, through the same entry point and with the same settings:

- **In code:** `cli.AddPlugin<TEntry>(options)`. The options are the defaults of the plugin's settings, with origin `code`.
- **From the dashboard:** installed at runtime from the marketplace (see the admin plugin's
  [server plugins](plugins/admin/README.md#server-plugins-from-the-marketplace)) and loaded at the next start.

Either way the plugin's settings appear on the dashboard's Config page, in `config list` and in MCP `config_*`. A
saved setting overrides the code's value, which overrides the default.

```csharp
builder.Services.AddControllers().AddCli(cli =>
{
    cli.ConfigureServerPlugins(o => o.DataDir = Path.Combine(builder.Environment.ContentRootPath, "qcli-data"));
    cli.AddPlugin<UsersPluginEntry>(new { protectServer = true });
    cli.AddPlugin<WeatherPluginEntry>(new { defaultLocation = "Paris" });
    cli.AddPlugin<PostgresPluginEntry>(new { name = "app" });   // its connection: config set data-explorer-postgres.settings.connectionString ...
});
```

### The entry contract

A plugin package declares its entry with an assembly attribute. The entry registers what the host would register in code:

```csharp
[assembly: CliPlugin(typeof(WeatherPluginEntry), Id = "weather", Order = CliPluginOrder.Default)]

public sealed class WeatherPluginEntry : ICliPluginEntry
{
    public void Register(CliPluginContext context)
    {
        var settings = context.Settings(new() { Title = "Weather", Settings =
        [
            new() { Key = "defaultLocation", Type = CliConfigEntryTypes.String, Default = "London" },
        ] });
        context.Builder.AddModule(new WeatherModule(() => settings.GetValue("settings", "defaultLocation")?.ToString()));
    }
}
```

`CliPluginContext` gives the plugin the following:

| Member | What it is |
|---|---|
| `PluginId`, `PackageName`, `Version` | Who it is |
| `Source` | `code` or `marketplace` |
| `Options` | The code options: `{ key: value }` or `{ section: { key: value } }` |
| `Builder`, `Services` | Where it registers |
| `Logger` | Category `Qodalis.Cli.Plugin:<id>`, buffered until logging starts |
| `DataDir` | `<data dir>/plugin-data/<id>`, created |
| `ServerDataDir` | The server's data directory |
| `Restart` | The restart service |

It also has three methods:

- **`Settings(options)`:** returns a `CliStoredConfigProvider`. Read its values lazily: they load when the server starts.
- **`BootSettings(options)`:** for settings needed while the services are registered, such as a config store's or
  a database's connection. They are kept in `plugin-settings.json` and read immediately.
- **`NeedsConfiguration(missing)`:** for a plugin that needs another plugin it builds on.

`[CliPlugin]` may also go on an entry class (the filesystem and data explorer entries live in the server itself).

**Load order.** Plugins added in code and installed plugins run together, after the host's own registrations, in
their declared order, then in the order they were added:

| Constant | Value | Used by |
|---|---|---|
| | 5 | users-efcore |
| `CliPluginOrder.Auth` | 10 | users |
| `CliPluginOrder.ConfigStore` | 20 | config-redis, config-efcore |
| `CliPluginOrder.Core` | 30 | databases, the data explorer |
| `CliPluginOrder.Default` | 50 | most plugins |
| | 60 | portal |
| `CliPluginOrder.Source` | 70 | the data explorer sources |

**Statuses.** Each plugin ends up `loaded`, `failed`, `skipped` (the host already has the package) or
`needs-configuration`:

- **`needs-configuration`:** a `Required` setting has no value, or the plugin called `NeedsConfiguration`. The
  missing keys are listed in `missing` (`<section>.<key>` of the provider in `settings`, e.g. `settings.connectionString`). The plugin's settings stay registered so they
  can be filled in, and it stays idle until then. For example, a data explorer source adds nothing and a storage
  answers that it needs configuration. A change applies after a restart.
- **`failed`:** the plugin threw. Its registrations are rolled back, the error is logged and kept, and the server
  starts all the same.

The state is shown in these places:

- `CliServerPlugins.States()`: `{ name, package, version, status, error?, source, missing?, settings? }`.
- The installed plugins list (`GET /api/v1/qcli/plugins/installed`: `source`, `status`, `missing`, `settings`).
- `installed.json`, for installed plugins.

**Settings storage.** When the server keeps its settings in memory (no default store set), the plugins' settings go
in `<data dir>/plugin-settings.json` (mode 0600), so a value saved from the dashboard survives a restart. A host
store, or a config store plugin, takes over otherwise.

**Config entries** carry `required` and `origin` (`code`, `setting` or `default`): who decided the value. A secret
has an origin too, and its value is never returned.

### Plugins installed at runtime

`<data dir>/server-plugins/installed.json` lists the installed plugins:

```json
{ "plugins": [ { "name": "weather", "package": "Qodalis.Cli.Plugin.Weather", "version": "1.0.0-beta.32",
  "integrity": "sha512-...", "title": "Weather", "installedAt": "...", "installedBy": "admin",
  "status": "loaded", "settings": "weather" } ] }
```

The data directory defaults to the `QCLI_DATA_DIR` environment variable, else `qcli-data` in the working directory; set
it with `cli.ConfigureServerPlugins(o => o.DataDir = ...)`. It holds everything the server writes at runtime:
`server-plugins/`, the admin plugin's `dashboard-plugins/`, `plugin-data/<id>/` and `plugin-settings.json`, so it must be
writable. In a container that runs as a non-root user, point it at a volume. When the admin marketplace is on and a
plugins folder cannot be created or written, the server logs one startup WARNING naming it, and installing, updating or
removing a plugin answers 500 with `{ "error": "cannot write the plugins folder <folder>: permission denied (EACCES).
Set DataDir (or QCLI_DATA_DIR) to a writable folder" }` (the `admin dashboard|plugins install` commands print the same).
The admin plugin's marketplace writes this file. Each package's assemblies live in `server-plugins/<package>/<version>/`.

At startup, every package loads into a collectible `CliPluginLoadContext`. An assembly the host already has
resolves to the host's copy, so the contracts exist once and a plugin never replaces a host assembly. These are
`Qodalis.Cli*`, the framework, and any package the host references.

`ConfigureServerPlugins(o => o.Enabled = false)` turns runtime plugins off. Plugins added in code still load.

A package that needs the host's code declares
`[assembly: AssemblyMetadata("QodalisCliAutoInstall", "false")]` (with `QodalisCliAutoInstallReason`). It is listed
in the marketplace but never installed.

### Official entries

| Plugin | Entry | Order | Settings (required ones marked *) |
|---|---|---|---|
| weather | `WeatherPluginEntry` | 50 | defaultLocation |
| aws | `AwsPluginEntry` | 50 | region, profile, accessKeyId and secretAccessKey (secrets), applied when changed |
| docs | `DocsPluginEntry` | 50 | folder, basePath, title, name (served without `UseQodalisDocs`) |
| audit | `AuditPluginEntry` | 50 | maxEntries |
| filesystem | `FileSystemPluginEntry` (in the server) | 50 | roots*, readOnly |
| filesystem-json, -sqlite, -s3, -efcore | `JsonFilePluginEntry`, `SqlitePluginEntry`, `S3PluginEntry`, `FileSystemEfCorePluginEntry` | 50 | file / database / bucket* and keys / dialect and connectionString*, readOnly (all read-only by default) |
| data explorer | `DataExplorerPluginEntry` (in the server) | 30 | forceReadOnly, maxRows, rateLimitQueries, rateLimitWindowSeconds (for the sources added after it) |
| data-explorer-postgres, -mysql, -mssql, -redis, -mongo, -elasticsearch, -sql | `PostgresPluginEntry`, ... | 70 | name, description, connectionString* (node* for Elasticsearch, database* for MongoDB, filename for SQLite), readOnly, timeoutMs, maxRows |
| users | `UsersPluginEntry` | 10 | protectServer, allowNonExpiringTokens, defaultAdminPassword (provider `users-plugin`; its middleware first in the pipeline) |
| users-efcore, audit-efcore, jobs-efcore, public-api-efcore, config-efcore | `UsersEfCorePluginEntry`, ... | 5 / 50 / 50 / 50 / 20 | dialect (sqlite, postgres, mssql), connectionString*, tablePrefix |
| config-redis | `RedisConfigPluginEntry` | 20 | url*, keyPrefix, ttlSeconds (kept in `plugin-settings.json`, never in Redis) |
| databases | `DatabasesPluginEntry` | 30 | name, dialect, connectionString*, migrationsFolder, applyOnStartup |
| jobs | `JobsPluginEntry` | 50 | its `jobs` provider (history retention) |
| public-api | `PublicApiPluginEntry` | 50 | basePath, title, serverUrl, requestsPerDay, requestsPerMinute, usageRetentionDays (needs the users plugin) |
| portal | `PortalPluginEntry` | 60 | path, name, logoUrl (needs docs or public-api) |
| logs-seq | `SeqLogsPluginEntry` | 50 | serverUrl*, apiKey, serviceName, minimumLevel, readHistory |

A few entries find what they depend on at runtime, from the host or from packages installed with the plugin:

- The EF Core entries find their dialect's provider (`Microsoft.EntityFrameworkCore.Sqlite`,
  `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.SqlServer`).
- The databases entry finds its ADO.NET driver (`Microsoft.Data.Sqlite`, `Npgsql`, `MySqlConnector`,
  `Microsoft.Data.SqlClient`).

Packages that are part of the server (`Qodalis.Cli`, and with it the filesystem and data explorer) are added in code only.

## Use with your app's sign-in

When your application already signs users in (cookies, JWT, an identity provider), the users plugin can take its users from your app instead of its own logins. The browser terminal, the MCP endpoint and the admin dashboard then all use your session.

```csharp
builder.Services.AddControllers().AddCli(cli =>
{
    // Your app's origin: the browser terminal and the dashboard call the API and open sockets from it
    cli.SetAllowedOrigins("https://app.example.com");

    cli.AddUsers(options => options
        // Runs on every CLI request and socket upgrade, and again on open sockets (see below)
        .UseExternalAuth(async (context, cancellationToken) =>
        {
            if (context.User.Identity?.IsAuthenticated != true) return null;
            var account = await accounts.FindAsync(context.User, cancellationToken);
            if (account is null) return null;
            return new ExternalAuthResult
            {
                User = new User { Id = account.Id, Name = account.Email, Email = account.Email, Disabled = account.Disabled },
                Permissions = account.CliPermissions,   // e.g. ["commands:execute", "logs:read"]
                IsAdmin = account.IsAdmin,
            };
        })
        // Your app owns accounts and roles: the CLI's user and group endpoints answer 501
        .ReadOnlyUsers()
        .ReadOnlyGroups());

    cli.AddAdmin(o => o.SignInUrl = "https://app.example.com/sign-in");
});

var app = builder.Build();

app.UseWebSockets();
app.UseCors();
app.UseAuthentication();   // your sign-in: sets HttpContext.User for the adapter
app.UseQodalisUsers();     // after CORS and authentication, before UseAuthorization and UseCli
app.UseQodalisAdmin();     // after UseWebSockets, before MapControllers
app.UseAuthorization();
app.MapControllers();
app.UseCli();
```

The order matters: the users middleware reads `HttpContext.User`, so your authentication must run first, and it must run before `UseCli()`, which serves the CLI routes and sockets.

- **The adapter** (`UseExternalAuth`) returns the user for a request, or `null` when it is not signed in. It can be async, so it can load the account, its roles and its disabled state from your database. A user returned with `Disabled = true` is refused.
- **Permissions:** a command's `RequiredPermissions` are checked against the user's permissions: those in `ExternalAuthResult.Permissions`, those of the groups the user is in (`User.Groups`, from the group store), and every permission when `IsAdmin` is set. Running commands needs `commands:execute`, the logs stream needs `logs:read`.
- **WebSockets:** the browser cannot send headers on a WebSocket, so clients first get a single-use ticket (`POST /api/v1/qcli/sessions/ws-ticket` with your session) and connect with `?ticket=`; the web CLI does this by itself. Open sockets are checked again through the adapter every `CliShellOptions.SessionCheckInterval` (30 s by default, at most 60 s) and closed with `4401` when the user is gone, disabled, or lost the socket's permission.
- **Read-only stores:** with `ReadOnlyUsers()` / `ReadOnlyGroups()` the CLI's create, update and delete endpoints answer `501` instead of writing; the session response carries `usersReadOnly` / `groupsReadOnly` so clients can hide those commands.
- **Admin dashboard:** `AddAdmin` uses the users plugin when present, and with an external adapter the dashboard signs in through your app: `GET /api/v1/qcli/auth/mode` answers `external` with `SignInUrl`, its own username/password form is off, and your app opens it as `/qcli/admin/#token=<your bearer token>` (a URL fragment, never sent to servers or logs). See [plugins/admin/README.md](plugins/admin/README.md#sign-in-with-the-host-apps-session).

Details: [plugins/users/README.md](plugins/users/README.md#external-auth-adapter).

## Endpoints

| Method | Path | Description |
|---|---|---|
| GET | `/api/qcli/versions` | Version discovery (supported and preferred versions) |
| GET | `/api/v1/qcli/version` | Server version |
| GET | `/api/v1/qcli/capabilities` | OS, shell and enabled features |
| GET | `/api/v1/qcli/commands` | Registered commands |
| GET | `/api/v1/qcli/commands/stats` | Executions per root command since start (count, failures, mean duration, last times) |
| POST | `/api/v1/qcli/execute` | Execute a command |
| POST | `/api/v1/qcli/execute/stream` | Execute with streamed output (SSE) |
| GET | `/api/qcli/fs/ls`, `cat`, `stat`, `download` | File system, with `AddFileSystem()` |
| POST | `/api/qcli/fs/upload`, `mkdir` | File system |
| DELETE | `/api/qcli/fs/rm` | File system |
| GET | `/api/qcli/data-explorer/sources`, `schema` | Data explorer, with a provider registered |
| POST | `/api/qcli/data-explorer/execute` | Run a query |
| POST | `/api/qcli/mcp` | MCP endpoint, with `AddMcp()` |
| GET, PATCH | `/api/v1/qcli/config[/{provider}[/{section}]]` | Config providers, with the admin plugin (see [Config providers](#config-providers)) |
| WS | `/ws/v1/qcli/events` | Server events (see [Server events](#server-events)) |
| WS | `/ws/v1/qcli/logs` | Log stream (`?level=` filter) |
| WS | `/ws/v1/qcli/shell` | Interactive PTY shell, with `EnableShell()` |

Every WebSocket is also served without the version segment (`/ws/qcli/events`, ...). The jobs, admin, audit and users plugins add routes under `/api/v1/qcli` (see their READMEs), among them the dashboard's chart data: `jobs/stats`, `logs/histogram` and `status/history`.

## Server events

Every server has one event bus, `ICliEventBus` (`Qodalis.Cli.Abstractions.Events`, registered by `AddCli`). Plugins
publish their changes on it, and your app can publish its own events and react to any of them in code. The events
WebSocket (`/ws/v1/qcli/events`) is one subscriber: it sends each event to the sockets of its audience as one flat
JSON object.

```json
{ "type": "job:completed", "timestamp": "2026-11-06T10:15:00.000Z", "jobId": "cleanup", "executionId": "…", "duration": 1520 }
```

```csharp
using Qodalis.Cli.Abstractions.Events;

var bus = app.Services.GetRequiredService<ICliEventBus>();

// React in code: "*" is every event, "user:*" every type starting with "user:", anything else one type.
// In-process handlers get every event whatever its audience; a handler's exception is logged and never fails the publisher.
using var subscription = bus.Subscribe("user:*", async (e, ct) =>
    logger.LogInformation("{Type} {User} by {Actor}", e.Type, e.Data["username"], e.Data.GetValueOrDefault("actor")));

// Publish: the fields (anonymous object, dictionary or POCO) are serialized camelCase and merged into the message.
await bus.PublishAsync("plan:changed", new { planId = "pro", actor = "alice" },
    CliEventAudience.WithPermission("plans:read"));
```

**Audience** (not sent on the wire): everyone (the default), `CliEventAudience.WithPermission("jobs:read")` (sockets
whose user has the permission), `CliEventAudience.User(userId)` (that user's sockets) or both,
`new CliEventAudience("tokens:read", userId)` (the user or anyone with the permission). On a server without
authentication every socket receives every event. A socket keeps the permissions it was opened with.

**Catalog** (published at the layer every caller goes through: REST API, MCP tools and commands alike, so a host's own
job manager, user store or file storage provider gets them too; `actor` is the user name when known):

| Type | Fields | Audience |
|---|---|---|
| `job:started` | jobId, executionId | `jobs:read` |
| `job:completed` | jobId, executionId, duration | `jobs:read` |
| `job:failed` | jobId, executionId, error | `jobs:read` |
| `job:cancelled`, `job:timed_out` | jobId, executionId | `jobs:read` |
| `job:retrying` | jobId, executionId, attempt, maxRetries, delayMs | `jobs:read` |
| `job:paused`, `job:resumed`, `job:stopped` | jobId, status (`paused`, `active`, `stopped`) | `jobs:read` |
| `job:triggered`, `job:updated` | jobId | `jobs:read` |
| `job:runs-deleted` | jobId, executionId (one run) or count (bulk) | `jobs:read` |
| `user:created`, `user:updated`, `user:deleted` | userId, username | `users:read` |
| `group:created`, `group:updated`, `group:deleted` | group | `groups:read` |
| `token:created`, `token:revoked` | tokenId, name, userId | `tokens:read` or the owner |
| `session:opened`, `session:closed` | userId, username, sessionId | `sessions:read` |
| `fs:changed` | action (`upload`, `mkdir`, `delete`, `write`, `move`, `copy`), path, newPath (move, copy) | `files:read` |
| `command:executed` | command (the processor name only, never arguments), success, durationMs, userId | `audit:read` |
| `audit:recorded` | the audit entry as the audit API returns it (its time is the event's `timestamp`) | `audit:read` |
| `log:entry` | timestamp, level, message, source (a log line of this process; admin plugin) | `logs:read` |

`user:updated` covers profile, password, enabled/disabled and group membership changes; `session:closed` covers
sign-out, expiry and revocation. The control messages `connected`, `disconnect` and `session-kicked` are unchanged.
`ICliEventSocketManager.BroadcastMessageAsync` and `SendToUserAsync` still send raw messages.

## Plugins

| Package | Description | Setup |
|---|---|---|
| [`Qodalis.Cli.Plugin.FileSystem`](plugins/filesystem) | Storage abstraction with in-memory and OS providers (included with `Qodalis.Cli`) | `cli.AddFileSystem(o => o.UseOsFileSystem(os => os.AllowedPaths.Add("/data")))` |
| [`Qodalis.Cli.Plugin.FileSystem.Json`](plugins/filesystem-json) | JSON file storage | `o.UseJsonFile(...)` |
| [`Qodalis.Cli.Plugin.FileSystem.Sqlite`](plugins/filesystem-sqlite) | SQLite storage | `o.UseSqlite(...)` |
| [`Qodalis.Cli.Plugin.FileSystem.EfCore`](plugins/filesystem-efcore) | Any EF Core database | `o.UseEfCore(...)` |
| [`Qodalis.Cli.Plugin.FileSystem.S3`](plugins/filesystem-s3) | Amazon S3 or compatible | `o.UseS3(...)` |
| [`Qodalis.Cli.Plugin.DataExplorer.Sql`](plugins/data-explorer-sql) | SQLite queries | `cli.AddDataExplorerSql(...)` |
| [`Qodalis.Cli.Plugin.DataExplorer.Postgres`](plugins/data-explorer-postgres) | PostgreSQL | `cli.AddDataExplorerPostgres(...)` |
| [`Qodalis.Cli.Plugin.DataExplorer.Mysql`](plugins/data-explorer-mysql) | MySQL | `cli.AddDataExplorerMysql(...)` |
| [`Qodalis.Cli.Plugin.DataExplorer.Mssql`](plugins/data-explorer-mssql) | SQL Server | `cli.AddDataExplorerMssql(...)` |
| [`Qodalis.Cli.Plugin.DataExplorer.Mongo`](plugins/data-explorer-mongo) | MongoDB | `cli.AddDataExplorerMongo(...)` |
| [`Qodalis.Cli.Plugin.DataExplorer.Redis`](plugins/data-explorer-redis) | Redis | `cli.AddDataExplorerRedis(...)` |
| [`Qodalis.Cli.Plugin.DataExplorer.Elasticsearch`](plugins/data-explorer-elasticsearch) | Elasticsearch | `cli.AddDataExplorerElasticsearch(...)` |
| [`Qodalis.Cli.Plugin.Jobs`](plugins/jobs) | Scheduled background jobs (cron or interval, retries, overlap policy, `MaxConcurrentRuns`) | `cli.AddJob<T>(...)` |
| [`Qodalis.Cli.Plugin.Jobs.EfCore`](plugins/jobs-efcore) | EF Core job history storage | `cli.AddEfCoreJobStorage(...)` |
| [`Qodalis.Cli.Plugin.Databases`](plugins/databases) | Databases (PostgreSQL, MySQL, SQL Server, SQLite): size, connections, load and largest tables, and migrations applied under a lock, tracked, run from the dashboard, terminal, MCP and API | `cli.AddDatabases(...)` |
| [`Qodalis.Cli.Plugin.Config.EfCore`](plugins/config-efcore) | The config providers' settings in a database (table `QcliConfig`) | `cli.AddEfCoreConfigStore(..., providerId?)` |
| [`Qodalis.Cli.Plugin.Config.Redis`](plugins/config-redis) | The config providers' settings in Redis: one hash per section, versioned writes checked atomically | `cli.AddRedisConfigStore(o => o.Url = ..., providerId?)` |
| [`Qodalis.Cli.Plugin.Databases.EfCore`](plugins/databases-efcore) | A DbContext's EF Core migrations in the databases plugin; the EF Core stores' schemas as sources | `d.AddEfCore<AppDbContext>("main")` |
| [`Qodalis.Cli.Plugin.Users`](plugins/users) | Users, sessions, groups, permissions, API tokens | `cli.AddUsers(...)` + `app.UseQodalisUsers()` |
| [`Qodalis.Cli.Plugin.Audit`](plugins/audit) | Audit log: who did what and when (the users plugin adds it) | `cli.AddAudit()` |
| [`Qodalis.Cli.Plugin.Audit.EfCore`](plugins/audit-efcore) | EF Core audit log storage | `cli.AddEfCoreAuditStore(...)` |
| [`Qodalis.Cli.Plugin.Users.EfCore`](plugins/users-efcore) | EF Core API token storage | `cli.AddEfCoreApiTokenStore(...)` |
| [`Qodalis.Cli.Plugin.PublicApi`](plugins/public-api) | Public API: host routes guarded by host tokens with per-account quotas, usage per token, the public OpenAPI document and Swagger UI | `cli.AddPublicApi(...)` + `app.UseQodalisPublicApi()` + `.RequirePublicApi("rates:read")` |
| [`Qodalis.Cli.Plugin.PublicApi.EfCore`](plugins/public-api-efcore) | EF Core usage counters, shared by every process | `cli.AddEfCorePublicApiUsageStore(...)` |
| [`Qodalis.Cli.Plugin.Docs`](plugins/docs) | Markdown guides from folders or code, served as JSON and read with `docs` | `cli.AddDocs(...)` + `app.UseQodalisDocs()` |
| [`Qodalis.Cli.Plugin.Portal`](plugins/portal) | The documentation site (guides, API reference) at one mount | `cli.AddPortal(...)` + `app.UseQodalisPortal()` |
| [`Qodalis.Cli.Plugin.Logs.Seq`](plugins/logs-seq) | Logs in Seq: persistent, shared by several services, the dashboard's log history | `cli.AddSeqLogs(...)` |
| [`Qodalis.Cli.Plugin.Admin`](plugins/admin) | Admin dashboard at `/qcli/admin` and the `admin` command | `cli.AddAdmin(...)` + `app.UseQodalisAdmin()` |
| [`Qodalis.Cli.Plugin.Weather`](plugins/weather) | Example module (`weather current`, `weather forecast`) | `cli.AddModule(new WeatherModule())` |
| [`plugins/aws`](plugins/aws) | AWS commands (S3, EC2, Lambda, CloudWatch, SNS, SQS, IAM, DynamoDB, ECS) | `cli.AddModule(new AwsModule())` |

Each plugin folder has a README with its options and namespaces (for example, the `UseOsFileSystem` / `UseInMemory` helpers come from `using Qodalis.Cli.Plugin.FileSystem;`). Custom file storage implements `IFileStorageProvider` (`o.Provider = new MyProvider()`); custom data sources implement `IDataExplorerProvider`.

### Plugin commands

Every plugin README has a `Terminal commands` section. Commands served by this server (registered by the plugin's
`Add*` method or `cli.AddModule`; permissions enforced on a protected server, along the command chain):

| Plugin | Commands | Permission |
|---|---|---|
| [admin](plugins/admin/README.md#terminal-commands) | `admin status [--history]`, `admin plugins [list\|enable\|disable <id>]`, `admin clients`, `admin logs [--level] [--search] [--service] [--since] [-n]` | `admin:access`; `logs` also `logs:read` |
| [databases](plugins/databases/README.md#terminal-commands) | `databases list\|stats\|migrations\|runs\|up\|down` | `databases:read`; `up`/`down` `databases:migrate` |
| [docs](plugins/docs/README.md#terminal-commands) | `docs list [--group]`, `docs read <slug>` | none |
| [public-api](plugins/public-api/README.md#terminal-commands) | `api usage [--user <name\|id>] [--days N]` | none for your own; `tokens:read` for another account |
| [aws](plugins/aws/README.md#terminal-commands) | `aws configure\|status\|s3\|ec2\|lambda\|cloudwatch\|sns\|sqs\|iam\|ecs\|dynamodb ...` | `aws:read`; changes also `aws:write` |
| [weather](plugins/weather/README.md#terminal-commands) | `weather [current\|forecast] [location]` | none |

Lists take `--json`. Server settings are the core `config` command ([Config providers](#config-providers)). Plugins
driven by web CLI plugins: jobs (`server jobs ...`, `@qodalis/cli-server-jobs`), users and audit (`adduser`, `token`,
`link approve`, `audit`, ..., `@qodalis/cli-users`), data explorer (`data-explorer`, `@qodalis/cli-data-explorer`),
filesystem (`scp`, `@qodalis/cli-scp`), live logs (`server logs`, `@qodalis/cli-server-logs`).

### Data explorer sources

Sources are **read-only by default**: set `ReadOnly = false` on a source that must write. The server checks each query before the provider runs it:

- SQL: one statement starting with `SELECT`, `WITH`, `EXPLAIN`, `SHOW`, `DESCRIBE`, `VALUES` or `TABLE`, without write keywords (`COMMIT` and `ROLLBACK` too) and without a carriage return that is not followed by a line feed. Comments and strings are ignored as MySQL, PostgreSQL, SQL Server and SQLite each read them: a query must pass under every reading.
- MongoDB (`Shell` or `Json` language): `show collections` or `db.<collection>.find|findOne|countDocuments|estimatedDocumentCount|distinct|aggregate(...)` without `$out`/`$merge` (JSON `\u` escapes decoded).
- Redis: read commands only (`GET`, `HGETALL`, `LRANGE`, `KEYS`, `SCAN`, ...).
- Elasticsearch: `GET`/`HEAD`, or `POST` to `_search`, `_count`, `_msearch`, `_mget`, `_field_caps`, `_validate/query` after index names only (not `/logs/_doc/_search`).
- Other languages are refused on read-only sources, unless the provider only reads (`IDataExplorerProvider.ReadOnlyQueries`).

The SQL providers also run the query in a read-only transaction (SQLite `query_only`, PostgreSQL `SET TRANSACTION READ ONLY`, MySQL `START TRANSACTION READ ONLY`, SQL Server a transaction that is always rolled back). PostgreSQL sends a read-only query as a single batch command, so it runs one statement at most.

SQL Server runs the statements of a batch without a `;` between them, so the one-statement rule cannot hold there: the write keywords (`COMMIT` and `ROLLBACK` included, so the batch cannot leave the rolled-back transaction) are what keeps a second statement out. Statements outside that list still run inside the transaction, and the rollback does not undo all of them (`NEXT VALUE FOR` sequences, `WAITFOR`, and with enough privileges `DBCC`, `KILL` or `SHUTDOWN`): give the source an account that can only read.

| Option | Default | Effect |
|---|---|---|
| `RequiredPermissions` | none | The caller needs them all (besides `data:query`); other callers do not see the source in `/sources` or `data_sources`, and `schema`, `execute` and the MCP tools answer 403 (MCP: an error result). Ignored on an open server |
| `ReadOnly` | `true` | The rules above; refusals are failed results with `errorCode: "read_only"` |
| `AllowedObjects` | all | Globs (`*`, case-insensitive) of tables (`schema.table` or `table`), collections, Redis keys or Elasticsearch indices; the schema lists only those, and a query naming another object is refused (`object_not_allowed`) |
| `MaskColumns` | none | Globs of columns/fields whose values become `***` (`null` stays `null`), nested document fields too; SQL queries may select them only by their own name (`masked_column`, below) |
| `DropColumns` | none | Globs of columns/fields removed from results and from the schema |
| `Audit` | `true` | Every execute (refusals too) is written to the audit log as `data_query` when the server has one (the users or audit plugin): `Details` is the source, `Properties` hold `source`, `query` (first 2000 characters), `success`, `rowCount`, `durationMs` and `error` |
| `RateLimit` | none | `new DataExplorerRateLimit { MaxQueries = 30, WindowSeconds = 60 }`: executes per caller (user id, or client address when anonymous) and source; more answer 429 |
| `ExportPermissions` | none | Needed to export results (`json`/`csv` in the web CLI); `/sources` reports `canExport`. Advisory |

`GET /sources` adds `readOnly` and `canExport` to each source. `AllowedObjects` and the read-only check are a convenience filter: give the source a database account that can only read what its users may see, since that account is the real boundary.

`AllowedObjects` reads the tables after `FROM` (every item of the list), `JOIN`, `INTO`, `UPDATE`, `TABLE`, `USING` and `DESCRIBE`, the collections of `$lookup`, `$graphLookup`, `$unionWith`, `$out` and `$merge`, and refuses Elasticsearch bodies that name an index (`_msearch`, `_mget`, terms lookups). The schema lists the tables of the default schema by name, so write patterns without a schema (`invoices`) unless the queries qualify the names; a pattern with a schema (`public.invoices`) matches only queries that name it so.

`MaskColumns` and `DropColumns` match the column and field names of the result, and fields of JSON text in a cell. On a SQL source with either option, a query may name a masked or dropped column only as a plain item of the select list (`SELECT email, name FROM users`), and the result must show it under its own name; anything else (`email AS e`, `upper(email)`, `WHERE email = ...`, `ORDER BY email`, a `UNION`, a whole row such as `row_to_json(u)`, a `U&"..."` name) is refused with `errorCode: "masked_column"`, and a result without the column is withheld. `SELECT *` is masked as usual. This is still a filter on the query text (database errors can quote values, and writable sources cannot name those columns outside the select list either): keep data that must stay hidden out of the account's reach (a view, column grants).

```csharp
cli.AddDataExplorerPostgres(connectionString, o =>
{
    o.Name = "billing";
    o.Description = "Billing database";
    o.RequiredPermissions = ["data:billing"];
    o.AllowedObjects = ["invoices", "customers"];
    o.MaskColumns = ["email", "*phone*"];
    o.DropColumns = ["password_hash"];
    o.RateLimit = new DataExplorerRateLimit { MaxQueries = 30, WindowSeconds = 60 };
    o.ExportPermissions = ["data:export"];
});
```

## Dashboard extensions

A plugin can add its own pages to the admin dashboard. It serves an ES module (built with
`@qodalis/cli-server-dashboard-kit`, so it looks like the dashboard) and registers it; the dashboard lists the
page in its navigation for users holding `Permission`, loads the module when the page is opened and mounts it.
Contract: [cli-server-dashboard `docs/extensions.md`](https://github.com/qodalis-solutions/cli-server-dashboard/blob/develop/docs/extensions.md).

```csharp
builder.Services.AddControllers().AddCli(cli => cli
    .AddAdmin()
    .AddDashboardExtension(new CliDashboardExtension
    {
        Id = "billing",                          // /qcli/admin/x/billing
        Title = "Billing",
        Titles = new Dictionary<string, string> { ["de"] = "Abrechnung" },
        Icon = "💳",
        Group = "data",                          // overview, operations, data, access, system; account: the account menu
        Permission = "billing:read",
        Entry = "/billing/admin/extension.js",   // served by your plugin, same origin
        Styles = ["/billing/admin/extension.css"],
    }));
```

The admin plugin lists them at `GET /api/v1/qcli/dashboard/extensions` (behind the dashboard's sign-in). Every
page is listed whatever the caller's permissions; the dashboard hides what the user may not see and the plugin's
own API enforces access. Invalid entries fail startup (`invalid dashboard extension: <reason>`,
`dashboard extension already registered: <id>`).

Two optional fields: `Widgets` (`["overview"]`: the module's cards on the dashboard's Overview; `["palette"]`: entries
in its command palette) and `Search` (`"/?q="`: the palette offers "Search <title> for …" and opens the page with the
text).

### Dashboard modules

The dashboard's plugin pages are modules (`@qodalis/cli-dashboard-module-<id>`, built in cli-server-dashboard's
`modules/`), each embedded in the package that owns its API and registered with `AddDashboardModule`:

| Page | Package | Registered by | Served at |
|---|---|---|---|
| Jobs (Overview cards, palette) | `Qodalis.Cli.Plugin.Jobs` | `AddJob` / `ConfigureJobs` | `/qcli/modules/jobs` |
| Audit | `Qodalis.Cli.Plugin.Audit` | `AddAudit` (also through `AddUsers`) | `/qcli/modules/audit` |
| Users (users, groups, sessions) | `Qodalis.Cli.Plugin.Users` | `AddUsers` | `/qcli/modules/users` |
| Account (account menu; the users build) | `Qodalis.Cli.Plugin.Users` | `AddUsers` | `/qcli/modules/account` |
| API tokens | `Qodalis.Cli.Plugin.Users` | `AddUsers` | `/qcli/modules/tokens` |
| Filesystem | `Qodalis.Cli` | `AddFileSystem` | `/qcli/modules/filesystem` |
| Data explorer | `Qodalis.Cli` | `AddDataExplorerProvider` | `/qcli/modules/data-explorer` |
| Databases (Overview cards) | `Qodalis.Cli.Plugin.Databases` | `AddDatabases` | `/qcli/databases` |

The files are served outside the API (no token; the page's API enforces access) with `Cache-Control: no-cache`, and
the extension's URLs carry their hash, so a new build loads at once. A package built without the module simply has
no page. `cli.ConfigureDashboardModule(id, fileProvider)` serves another build, and `null` leaves the page out. A page
built from another module's build (`CliDashboardModule.Build`, e.g. Account from `users`) follows that module's
override unless it has its own.

The extension group `account` lists a page in the dashboard's account menu (the signed-in user's menu) instead of the
sidebar.

A plugin of your own does the same: `cli.AddDashboardModule(new CliDashboardModule { Id, Title, … }, typeof(MyPlugin).Assembly)`
with the build under `wwwroot/<id>` (`<QcliDashboardModule Include="<id>" />` and `build/Qcli.DashboardModules.targets`
copy and embed it here), or `AddDashboardModule(module, fileProvider)`.

## Docker

```bash
docker run -p 8046:8046 ghcr.io/qodalis-solutions/cli-server-dotnet
```

The image runs the bundled host (`src/Qodalis.Cli.Server`) on port 8046. `./docker-run.sh` builds and starts the demo server instead (`docker compose`, port 8046). Set `QCLI_SHELL_TOKEN` to enable the bundled host's shell in token mode; without it the shell stays locked.

## Versioning

Every package in this repository (`Qodalis.Cli`, `Qodalis.Cli.Abstractions` and every `Qodalis.Cli.Plugin.*`) has the same version, and so do the Node and Python servers' packages: `1.0.0-beta.33` today. A release bumps all of them together, even packages that did not change, so one version number describes a whole server release. Use the same version for every `Qodalis.Cli*` package you reference.

## Development

```bash
dotnet build src/Qodalis.Cli.sln                                   # build everything
dotnet test tests/Qodalis.Cli.Tests/Qodalis.Cli.Tests.csproj        # xUnit tests
dotnet run --project src/Qodalis.Cli.Server/Qodalis.Cli.Server.csproj  # bundled host with Swagger UI
cd demo && dotnet run                                              # demo server on http://localhost:8046
```

| Path | Contents |
|---|---|
| `src/Qodalis.Cli.Abstractions` | Interfaces for plugin authors |
| `src/Qodalis.Cli` | ASP.NET Core integration: controllers, `CliBuilder`, services, MCP, security |
| `src/Qodalis.Cli.Server` | Bundled host (the built-in `http` command and the weather module) |
| `plugins/` | Plugin packages |
| `demo/` | Demo server: its example commands as the `demo` module (`echo`, `status`, `time`, `hello`, `math`, `system`, `hash`, `base64`), jobs |
| `tests/Qodalis.Cli.Tests` | xUnit tests |

Work on `feature/*` branches and open pull requests into `develop`. Pushes to `main` publish the NuGet packages and the Docker image.

## License

[MIT](LICENSE)
