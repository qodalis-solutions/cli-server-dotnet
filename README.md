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

`plugins/weather/` is a complete reference module.

## Configuration

All options are set on the `CliBuilder` passed to `AddCli(cli => ...)`.

| Method | Description |
|---|---|
| `SetAllowedOrigins(params string[])` | CORS and WebSocket Origin allow-list; `http://127.0.0.1:*` matches any port. Same-origin requests always pass. |
| `EnableShell()` / `EnableShell(Action<CliShellOptions>)` | Enables the interactive shell sockets (off by default) |
| `AddMcp(Action<McpOptions>?)` | Enables `POST /api/qcli/mcp` |
| `AddFileSystem(Action<FileSystemOptions>?)` | Enables `/api/qcli/fs/*` |
| `AddDataExplorerProvider<T>(...)` / `AddDataExplorerProvider(instance, ...)` | Registers a data explorer source |
| `AddProcessor`, `AddProcessorsFromAssembly`, `AddModule` | Registers commands |
| `AddApplicationPart(Assembly)` | Adds a plugin assembly's controllers |
| `Services` | The `IServiceCollection`, for plugin registrations |

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
| data explorer | `data_sources`, `data_schema`, `data_query` | `data:query` |
| jobs plugin | `jobs_list`, `jobs_get`, `jobs_history`, `jobs_trigger`, `jobs_pause`, `jobs_resume`, `jobs_stop`, `jobs_cancel` | `jobs:read` / `jobs:manage` |

With the users plugin, requests authenticate with a session or an API token (`Authorization: Bearer qcli_pat_...`) and `tools/list` only lists what the caller may call. Plugins add tools by implementing `IMcpToolProvider` and calling `services.AddMcpToolProvider<T>()`.

Connect Claude Code directly, or through the [`@qodalis/mcp-cli`](https://github.com/qodalis-solutions/mcp-cli) bridge:

```bash
claude mcp add --transport http qcli http://localhost:8046/api/qcli/mcp --header "Authorization: Bearer <token>"
claude mcp add qcli -e QCLI_URL=http://localhost:8046 -e QCLI_TOKEN=<token> -- npx -y @qodalis/mcp-cli
```

### Security

Without an authentication plugin the server is open. Install the users plugin (`cli.AddUsers()` and `app.UseQodalisUsers()` before `app.UseCli()`); with its default `ProtectServer = true` every CLI route and socket requires a session, except sign-in and version discovery. See [plugins/users/README.md](plugins/users/README.md).

## Endpoints

| Method | Path | Description |
|---|---|---|
| GET | `/api/qcli/versions` | Version discovery (supported and preferred versions) |
| GET | `/api/v1/qcli/version` | Server version |
| GET | `/api/v1/qcli/capabilities` | OS, shell and enabled features |
| GET | `/api/v1/qcli/commands` | Registered commands |
| POST | `/api/v1/qcli/execute` | Execute a command |
| POST | `/api/v1/qcli/execute/stream` | Execute with streamed output (SSE) |
| GET | `/api/qcli/fs/ls`, `cat`, `stat`, `download` | File system, with `AddFileSystem()` |
| POST | `/api/qcli/fs/upload`, `mkdir` | File system |
| DELETE | `/api/qcli/fs/rm` | File system |
| GET | `/api/qcli/data-explorer/sources`, `schema` | Data explorer, with a provider registered |
| POST | `/api/qcli/data-explorer/execute` | Run a query |
| POST | `/api/qcli/mcp` | MCP endpoint, with `AddMcp()` |
| WS | `/ws/v1/qcli/events` | Server-push events |
| WS | `/ws/v1/qcli/logs` | Log stream (`?level=` filter) |
| WS | `/ws/v1/qcli/shell` | Interactive PTY shell, with `EnableShell()` |

Every WebSocket is also served without the version segment (`/ws/qcli/events`, ...). The jobs and users plugins add routes under `/api/v1/qcli` (see their READMEs).

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
| [`Qodalis.Cli.Plugin.Jobs`](plugins/jobs) | Scheduled background jobs | `cli.AddJob<T>(...)` |
| [`Qodalis.Cli.Plugin.Jobs.EfCore`](plugins/jobs-efcore) | EF Core job history storage | `cli.AddEfCoreJobStorage(...)` |
| [`Qodalis.Cli.Plugin.Users`](plugins/users) | Users, sessions, groups, permissions, API tokens, audit log | `cli.AddUsers(...)` + `app.UseQodalisUsers()` |
| [`Qodalis.Cli.Plugin.Admin`](plugins/admin) | Admin dashboard at `/qcli/admin` | `cli.AddAdmin(...)` + `app.UseQodalisAdmin()` |
| [`Qodalis.Cli.Plugin.Weather`](plugins/weather) | Example module (`weather current`, `weather forecast`) | `cli.AddModule(new WeatherModule())` |
| [`plugins/aws`](plugins/aws) | AWS commands (S3, EC2, Lambda, CloudWatch, SNS, SQS, IAM, DynamoDB, ECS) | `cli.AddModule(new AwsModule())` |

Each plugin folder has a README with its options and namespaces (for example, the `UseOsFileSystem` / `UseInMemory` helpers come from `using Qodalis.Cli.Plugin.FileSystem;`). Custom file storage implements `IFileStorageProvider` (`o.Provider = new MyProvider()`); custom data sources implement `IDataExplorerProvider`.

## Docker

```bash
docker run -p 8046:8046 ghcr.io/qodalis-solutions/cli-server-dotnet
```

The image runs the bundled host (`src/Qodalis.Cli.Server`) on port 8046. `./docker-run.sh` builds and starts the demo server instead (`docker compose`, port 8046). Set `QCLI_SHELL_TOKEN` to enable the bundled host's shell in token mode; without it the shell stays locked.

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
| `src/Qodalis.Cli.Server` | Bundled host (built-in `echo`, `status`, `system`, `http`, `hash`, `base64` commands) |
| `plugins/` | Plugin packages |
| `demo/` | Demo server with sample processors and jobs |
| `tests/Qodalis.Cli.Tests` | xUnit tests |

Work on `feature/*` branches and open pull requests into `develop`. Pushes to `main` publish the NuGet packages and the Docker image.

## License

[MIT](LICENSE)
