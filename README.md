# Qodalis CLI Server (.NET)

[![NuGet Qodalis.Cli](https://img.shields.io/nuget/v/Qodalis.Cli?label=Qodalis.Cli)](https://www.nuget.org/packages/Qodalis.Cli)
[![NuGet Qodalis.Cli.Abstractions](https://img.shields.io/nuget/v/Qodalis.Cli.Abstractions?label=Qodalis.Cli.Abstractions)](https://www.nuget.org/packages/Qodalis.Cli.Abstractions)

A .NET CLI server framework for the [Qodalis CLI](https://github.com/qodalis-solutions/web-cli) ecosystem. Build custom server-side commands that integrate with the Qodalis web terminal.

## Packages

| Package | Purpose |
|---------|---------|
| `Qodalis.Cli.Abstractions` | Interfaces and base classes for writing command processors |
| `Qodalis.Cli` | ASP.NET Core integration (controllers, DI, WebSocket) |

### File Storage Plugins

| Package | Storage Backend | Dependencies |
|---------|----------------|--------------|
| `Qodalis.Cli.Plugin.FileSystem` | Core abstraction + InMemory + OS providers | — |
| `Qodalis.Cli.Plugin.FileSystem.Json` | JSON file persistence | — |
| `Qodalis.Cli.Plugin.FileSystem.Sqlite` | SQLite database | Microsoft.Data.Sqlite |
| `Qodalis.Cli.Plugin.FileSystem.EfCore` | Entity Framework Core (any DB) | Microsoft.EntityFrameworkCore |
| `Qodalis.Cli.Plugin.FileSystem.S3` | Amazon S3 | AWSSDK.S3 |

### Users Plugin

| Package | Description | Dependencies |
|---|---|---|
| `Qodalis.Cli.Plugin.Users` | Authentication, users, sessions, groups and audit log API (server side of `@qodalis/cli-users` server mode) | — |

### Cloud Plugins

| Package | Description | Dependencies |
|---------|-------------|--------------|
| `Qodalis.Cli.Plugin.Aws` | AWS cloud resource management (S3, EC2, Lambda, CloudWatch, SNS, SQS, IAM, DynamoDB, ECS) | AWSSDK.* |

## Installation

```bash
# For plugin authors (command processors only)
dotnet add package Qodalis.Cli.Abstractions

# For server hosts (full framework)
dotnet add package Qodalis.Cli
```

## Quick Start

```csharp
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        // CORS + WebSocket Origin allow-list (the library configures no CORS otherwise).
        cli.SetAllowedOrigins("http://localhost:4300");
        cli.AddProcessor<GreetCommandProcessor>();
        cli.AddProcessor<MathCommandProcessor>();
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

var app = builder.Build();
app.UseWebSockets();
app.UseCors();
app.MapControllers();
app.UseCli();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var eventSocketManager = app.Services.GetRequiredService<CliEventSocketManager>();
lifetime.ApplicationStopping.Register(() =>
{
    eventSocketManager.BroadcastDisconnectAsync().GetAwaiter().GetResult();
});

app.Run();
```

## Creating Custom Command Processors

### Simple Command

Extend `CliCommandProcessor` and implement `Command`, `Description`, and `HandleAsync`:

```csharp
using Qodalis.Cli.Abstractions;

public class GreetCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "greet";
    public override string Description { get; set; } = "Greets the user";

    public override Task<string> HandleAsync(
        CliProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        var name = command.Value ?? "World";
        return Task.FromResult($"Hello, {name}!");
    }
}
```

Register it during startup:

```csharp
builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        cli.AddProcessor<GreetCommandProcessor>();
    });
```

### Command with Parameters

Declare parameters with types, aliases, defaults, and required flags. The framework exposes them to the CLI client for autocompletion and validation.

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
            Required = false,
            Type = CommandParameterType.Boolean,
        },
        new CliCommandParameterDescriptor
        {
            Name = "format",
            Aliases = ["-f"],
            Description = "Date/time format string",
            Required = false,
            Type = CommandParameterType.String,
            DefaultValue = "yyyy-MM-dd HH:mm:ss",
        }
    ];

    public override Task<string> HandleAsync(
        CliProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        var useUtc = command.Args.ContainsKey("utc");
        var format = command.Args.TryGetValue("format", out var fmt)
            ? fmt.ToString()!
            : "yyyy-MM-dd HH:mm:ss";
        var now = useUtc ? DateTime.UtcNow : DateTime.Now;
        var label = useUtc ? "UTC" : "Local";

        return Task.FromResult($"{label}: {now.ToString(format)}");
    }
}
```

Parameter types: `String`, `Number`, `Boolean`, `Array`, `Object` (from `CommandParameterType` enum).

### Sub-commands

Nest processors to create command hierarchies like `math add --a 5 --b 3`:

```csharp
public class MathAddProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "add";
    public override string Description { get; set; } = "Adds two numbers";

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "a", Description = "First number",
            Required = true, Type = CommandParameterType.Number
        },
        new CliCommandParameterDescriptor
        {
            Name = "b", Description = "Second number",
            Required = true, Type = CommandParameterType.Number
        },
    ];

    public override Task<string> HandleAsync(
        CliProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        var a = double.Parse(command.Args["a"].ToString()!);
        var b = double.Parse(command.Args["b"].ToString()!);
        return Task.FromResult($"{a} + {b} = {a + b}");
    }
}

public class MathMultiplyProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "multiply";
    public override string Description { get; set; } = "Multiplies two numbers";

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "a", Description = "First number",
            Required = true, Type = CommandParameterType.Number
        },
        new CliCommandParameterDescriptor
        {
            Name = "b", Description = "Second number",
            Required = true, Type = CommandParameterType.Number
        },
    ];

    public override Task<string> HandleAsync(
        CliProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        var a = double.Parse(command.Args["a"].ToString()!);
        var b = double.Parse(command.Args["b"].ToString()!);
        return Task.FromResult($"{a} * {b} = {a * b}");
    }
}

public class MathCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "math";
    public override string Description { get; set; } = "Performs basic math operations";
    public override bool? AllowUnlistedCommands { get; set; } = false;

    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; } =
    [
        new MathAddProcessor(),
        new MathMultiplyProcessor(),
    ];

    public override Task<string> HandleAsync(
        CliProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult("Usage: math add|multiply --a <number> --b <number>");
    }
}
```

## Registration Methods

The `CliBuilder` supports four registration approaches:

```csharp
builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        // 1. Register by type (resolved as singleton)
        cli.AddProcessor<GreetCommandProcessor>();

        // 2. Register a pre-built instance
        cli.AddProcessor(new TimeCommandProcessor());

        // 3. Auto-discover all processors in an assembly
        cli.AddProcessorsFromAssembly(typeof(WeatherModule).Assembly);

        // 4. Register an entire module (bundles multiple processors)
        cli.AddModule(new WeatherModule());
    });
```

Assembly scanning registers every class implementing `ICliCommandProcessor` found in the given assembly.

## Modules

Modules group related command processors into a reusable unit. Implement `ICliModule` (or extend the `CliModule` base class) to bundle processors under a single name and version.

### Defining a Module

```csharp
using Qodalis.Cli.Abstractions;

public class WeatherModule : CliModule
{
    public override string Name => "weather";
    public override string Version => "1.0.0";
    public override string Description => "Provides weather information commands";

    public override IEnumerable<ICliCommandProcessor> Processors { get; } =
    [
        new CliWeatherCommandProcessor(),
    ];
}
```

### Registering a Module

```csharp
builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        cli.AddModule(new WeatherModule());
    });
```

`AddModule()` iterates over the module's `Processors` and registers each one, just like calling `AddProcessor()` for each individually.

### ICliModule Interface

| Member | Type | Description |
|--------|------|-------------|
| `Name` | `string` | Unique module identifier |
| `Version` | `string` | Module version |
| `Description` | `string` | Short description |
| `Author` | `ICliCommandAuthor` | Author metadata (defaults to library author) |
| `Processors` | `IEnumerable<ICliCommandProcessor>` | Command processors provided by the module |

### Example: Weather Module

The repository includes a weather module under `plugins/weather/` as a reference implementation. It registers a `weather` command with `current` and `forecast` sub-commands, using the [wttr.in](https://wttr.in) API:

```
weather                    # Shows current weather (default: London)
weather current London     # Current conditions for London
weather forecast --location Paris  # 3-day forecast for Paris
```

## Dependency Injection

Processors registered by type (`AddProcessor<T>()` or `AddProcessorsFromAssembly()`) are resolved through the ASP.NET Core DI container, so constructor injection works automatically:

```csharp
public class CliWeatherCommandProcessor : CliCommandProcessor
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CliWeatherCommandProcessor> _logger;

    public CliWeatherCommandProcessor(
        IHttpClientFactory httpClientFactory,
        ILogger<CliWeatherCommandProcessor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public override string Command { get; set; } = "weather";
    public override string Description { get; set; } = "Fetches current weather";

    public override async Task<string> HandleAsync(
        CliProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient();
        var city = command.Value ?? "London";
        _logger.LogInformation("Fetching weather for {City}", city);
        // ...
        return $"Weather in {city}: 22°C, Sunny";
    }
}
```

Register the processor and its dependencies:

```csharp
builder.Services.AddHttpClient();

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        cli.AddProcessor<CliWeatherCommandProcessor>();
    });
```

> **Note:** Processors are registered as **singletons**. If you need to use scoped services (e.g., `DbContext`), inject `IServiceScopeFactory` and create a scope inside `HandleAsync`:
>
> ```csharp
> public class CliUsersCommandProcessor : CliCommandProcessor
> {
>     private readonly IServiceScopeFactory _scopeFactory;
>
>     public CliUsersCommandProcessor(IServiceScopeFactory scopeFactory)
>     {
>         _scopeFactory = scopeFactory;
>     }
>
>     public override string Command { get; set; } = "users";
>     public override string Description { get; set; } = "Lists users";
>
>     public override async Task<string> HandleAsync(
>         CliProcessCommand command,
>         CancellationToken cancellationToken = default)
>     {
>         using var scope = _scopeFactory.CreateScope();
>         var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
>         var count = await db.Users.CountAsync(cancellationToken);
>         return $"Total users: {count}";
>     }
> }
> ```

Processors registered with `AddProcessor(instance)` bypass the DI container since the instance is already constructed.

## Command Input

Every processor receives a `CliProcessCommand` with the parsed command input:

| Property | Type | Description |
|----------|------|-------------|
| `Command` | `string` | Command name (e.g., `"time"`) |
| `Value` | `string?` | Positional argument (e.g., `"hello"` in `echo hello`) |
| `Args` | `Dictionary<string, object>` | Named parameters (e.g., `--format "HH:mm"`) |
| `ChainCommands` | `IEnumerable<string>` | Sub-command chain (e.g., `["add"]` in `math add`) |
| `RawCommand` | `string` | Original unprocessed input |
| `Data` | `object?` | Arbitrary data payload from the client |

## API Versioning

Processors declare which API version they target. The default is version 1.

```csharp
public class MyV2Processor : CliCommandProcessor
{
    public override string Command { get; set; } = "dashboard";
    public override string Description { get; set; } = "Server dashboard (v2 only)";
    public override int ApiVersion { get; set; } = 2;

    public override Task<string> HandleAsync(
        CliProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult("Dashboard data...");
    }
}
```

The server exposes versioned endpoints:

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/qcli/versions` | Version discovery (supported versions, preferred version) |
| GET | `/api/v1/qcli/version` | V1 server version |
| GET | `/api/v1/qcli/commands` | V1 commands (all processors) |
| POST | `/api/v1/qcli/execute` | V1 execute |
| POST | `/api/v1/qcli/execute/stream` | V1 execute with SSE streaming |
| GET | `/api/v1/qcli/capabilities` | Server capabilities (OS, shell, features) |
| WS | `/ws/v1/qcli/events` | WebSocket server-push event broadcast |
| WS | `/ws/v1/qcli/logs` | WebSocket log streaming (supports `?level=` filter) |
| WS | `/ws/v1/qcli/shell` | Interactive PTY shell session (opt-in: `cli.EnableShell()`) |

The interactive shell sockets (`/ws/v1/qcli/shell`, `/ws/qcli/shell`, `/ws/v1/qcli`, `/ws/qcli`) are **disabled by default** — see [Shell security](#shell-security). `cli.SetAllowedOrigins(...)` restricts CORS and every WebSocket upgrade to the listed origins (`http://127.0.0.1:*` matches any port).

### Security

Without an authentication plugin the server is open — protect it with the users plugin (`cli.AddUsers()` + `app.UseQodalisUsers()` before `app.UseCli()`), which by default (`ProtectServer = true`) requires a session on every CLI route and socket except login and version discovery. Processors can declare `RequiredPermissions`, enforced on `/execute` in addition to `commands:execute`. See [plugins/users/README.md](plugins/users/README.md) for the permission table and WebSocket tickets.

Same-origin requests (whose `Origin` equals the server's own `scheme://host[:port]`, e.g. the bundled admin dashboard at `/qcli/admin`) always pass the Origin checks (WebSocket upgrades and CSRF), even when that origin is not listed in `SetAllowedOrigins(...)`.

### Host check (DNS rebinding)

A web page on `attacker.example` can re-resolve its own name to your server's address (DNS rebinding). The browser then treats it as same-origin with the server, so Origin checks pass; only the `Host` header still names the attacker's domain. CLI routes (`/api/v1/qcli/*`, `/api/qcli/*`, MCP) and WebSockets (`/ws/...`) therefore reject requests whose `Host` is not allowed with **`421 Misdirected Request`** (`{"error":"misdirected request: host not allowed"}`).

Always allowed: `localhost`, `*.localhost` and any IP literal (port ignored). Other names must be listed:

```csharp
cli.SetAllowedHosts("cli.example.com", "*.corp.example"); // "*.x" matches subdomains only; "*" disables the check
```

By default (no `SetAllowedHosts`) the check is enforced only while the server is **unprotected** (no users plugin, or `ProtectServer = false`), because rebinding cannot carry the victim's credentials to a protected server. Once `SetAllowedHosts(...)` is called, it is enforced always. This is complementary to ASP.NET Core's `HostFiltering` middleware (`AllowedHosts` in `appsettings.json`), which applies to the whole app when configured.

### Shell security

The shell gives OS-level access, so it is off unless enabled, and enabling it does **not** open it:
exactly one access mode decides who may connect.

| Mode (`shellAuth`) | When | Client sends |
|---|---|---|
| `users` | the users plugin is installed (`cli.AddUsers()`) | `?ticket=` of a session holding `shell:execute` |
| `token` | `o.Token` is set (no users plugin) | `Authorization: Bearer <token>`, or `?_header_Authorization=Bearer%20<token>` (browser clients: the server's configured `headers`) |
| `custom` | `o.Authorize` is set (no users plugin) | whatever your callback checks (headers, query, `ctx.Connection.RemoteIpAddress`) |
| `anonymous` | only with `o.AllowAnonymous = true` | nothing — logs a startup WARNING; never use on a reachable network |
| `locked` | enabled but none of the above | every connection closed with `4401`, startup ERROR log |

With the users plugin, a configured `Token`/`Authorize` is required **in addition** to the session; outside users
mode, when both are set both must pass (`shellAuth` reports `token`). Tokens are compared in constant time
(SHA-256 + `CryptographicOperations.FixedTimeEquals`) and never logged. `GET /api/v1/qcli/capabilities` returns
`shell: true|false` and, when enabled, `shellAuth`.

**With the users plugin** (sessions, permissions, audit):

```csharp
cli.EnableShell();                 // users mode: ticket + shell:execute
cli.AddUsers(o => { /* ... */ });
```

An open shell belongs to the session that opened it: logout, idle/absolute expiry, admin revoke (kick), the user
being disabled or deleted, or losing `shell:execute` (re-checked once when the shell registers, before the process
starts, then every `SessionCheckInterval`, ≤ 60 s, and immediately on user/group changes) closes it with `4401 session ended` and kills the process tree. Shell input
counts as session activity (at most one touch per 30 s); re-checks alone never extend the session. The audit log records `shell_opened` / `shell_closed` with `userId`, `actorId` and
`metadata: { mode: interactive|command, sessionId, durationMs, exitCode, reason }` — never the command or stdin.
`reason` is `exited` (only then `exitCode`), `closed` (client), `session ended`, `permission removed`,
`shell timed out`, `server shutdown` or `error`. Server shutdown closes open shells with `1001`.

**Without the users plugin** use token mode (or `Authorize`):

```csharp
cli.EnableShell(o =>
{
    o.Token = builder.Configuration["Shell:Token"];   // long random secret
    // o.Authorize = ctx => ValueTask.FromResult(IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress!));
});
```

The bundled `Qodalis.Cli.Server` host does exactly this: set `QCLI_SHELL_TOKEN=<secret>` (or `Shell:Token` in
`appsettings.json`) to enable its shell; without a token it stays locked in every environment.
`AllowAnonymous` is ignored once `Token` or `Authorize` is set.

In the web-cli client, give the server entry `headers: { Authorization: 'Bearer <token>' }`; browsers cannot set
WebSocket headers, so the client forwards it as `_header_Authorization` (prefer TLS; ASP.NET Core request logging
at Information level includes query strings). Each open/close is logged at Information with the remote address
and mode (no command text).

**Limits, timeouts and environment** (all modes):

| Option | Default | |
|---|---|---|
| `MaxSessionsPerUser` | 3 | per user (users mode) or per remote address → close `4429` |
| `MaxSessions` | 20 | total concurrent shells → `4429` |
| `IdleTimeout` | 15 min | without stdin → `4408 shell timed out` (null = never) |
| `MaxDuration` | none | total lifetime → `4408` |
| `SessionCheckInterval` | 30 s | users mode re-validation (capped at 60 s) |
| `WorkingDirectory` | server user's home | |
| `Shell` | detected (`$SHELL`, bash, sh; PowerShell on Windows) | |
| `EnvironmentAllow` | — | extra server variables passed through |
| `EnvironmentSet` | — | variables set in the shell (applied last) |

The child gets a minimal environment — `PATH`, `HOME`, `USER`, `LANG`, `TERM`, `COLUMNS`, `LINES`, `SHELL` plus
`EnvironmentAllow`/`EnvironmentSet` — never the full server environment (connection strings and secrets stay out).

**Process cleanup.** Every shell gets a unique marker in its environment, `QCLI_SHELL_ID=<random 128-bit hex>`,
set after `EnvironmentAllow`/`EnvironmentSet` (configuration cannot override it) and inherited by everything the
shell starts. Whenever a shell ends while its process is running (client close, timeout, session end, server
shutdown), its processes are collected first (Linux: `/proc`, so no `ps` is needed; macOS:
`ps -A -o pid=,ppid=,pgid=,tty=`) and then signalled `SIGHUP` → `SIGTERM` → (2 s grace) → `SIGKILL`: every
descendant, including `&` jobs in their own process groups and `setsid`/`nohup` children, every process still on
the shell's terminal, every process whose environment carries the shell's marker (Linux: `/proc/<pid>/environ`;
macOS: `ps -A -E -ww`) — which catches daemons that double-forked and left the terminal (`setsid cmd &`) — and
their process groups (never the server's own). Marked processes are collected again right before `SIGKILL`.
Limitations: a process escapes only by deliberately unsetting `QCLI_SHELL_ID` or by running as another user
(its environment is unreadable); on macOS `ps` does not show the environment of Apple system binaries
(`/bin/sleep`, `/bin/zsh`, …), so there a detached system binary is not found by its marker. Jobs left behind by
a shell that exited on its own (`nohup cmd & exit`) are not touched. On Windows the process tree is killed directly.

Rejections are readable by browsers: the upgrade is accepted, then closed with a code and reason:
`4401 "login required"` (no ticket/token), `4401 "session ended"`, `4401 "shell access is not configured"` (locked),
`4403 "forbidden"` (missing `shell:execute`, wrong token, `Authorize` false or throwing), `4404 "shell disabled"`,
`4408 "shell timed out"`, `4429 "too many shell sessions"`. Only a disallowed `Origin` is still rejected with HTTP 403.

The Qodalis CLI client auto-negotiates the highest mutually supported version via the `/api/qcli/versions` discovery endpoint.

## Processor Base Class Reference

`CliCommandProcessor` provides these overridable members:

| Member | Type | Default | Description |
|--------|------|---------|-------------|
| `Command` | `string` | (required) | Command name |
| `Description` | `string` | (required) | Help text shown to users |
| `HandleAsync` | method | (required) | Execution logic |
| `Parameters` | `IEnumerable<ICliCommandParameterDescriptor>?` | `null` | Declared parameters |
| `Processors` | `IEnumerable<ICliCommandProcessor>?` | `null` | Sub-commands |
| `AllowUnlistedCommands` | `bool?` | `null` | Accept sub-commands not in `Processors` |
| `ValueRequired` | `bool?` | `null` | Require a positional value |
| `Version` | `string` | `"1.0.0"` | Processor version string |
| `ApiVersion` | `int` | `1` | Target API version |
| `Author` | `ICliCommandAuthor` | default author | Author metadata (name, email) |

## Built-in Processors

The `Qodalis.Cli.Server` package and demo include these sample processors:

| Command | Description |
|---------|-------------|
| `echo` | Echoes input text |
| `status` | Server status (uptime, OS, .NET version) |
| `system` | Detailed system information |
| `http` | HTTP request operations |
| `hash` | Hash computation (MD5, SHA1, SHA256, SHA512) |
| `base64` | Base64 encode/decode |

The demo app additionally includes `time`, `hello`, and `math` sample processors.

## File Storage

The server includes a pluggable file storage system exposed at `/api/qcli/fs/*`. Enable it with `AddFileSystem()` and choose a storage backend.

### Filesystem API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/qcli/fs/ls?path=/` | List directory contents |
| GET | `/api/qcli/fs/cat?path=/file.txt` | Read file content |
| GET | `/api/qcli/fs/stat?path=/file.txt` | File/directory metadata |
| GET | `/api/qcli/fs/download?path=/file.txt` | Download file |
| POST | `/api/qcli/fs/upload` | Upload file (multipart) |
| POST | `/api/qcli/fs/mkdir` | Create directory |
| DELETE | `/api/qcli/fs/rm?path=/file.txt` | Delete file or directory |

### Storage Providers

```csharp
builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        // In-memory (default) — files lost on restart
        cli.AddFileSystem(o => o.UseInMemory());

        // OS filesystem with path restrictions
        cli.AddFileSystem(o => o.UseOsFileSystem(os =>
        {
            os.AllowedPaths = new List<string> { "/tmp", "/app" };
        }));

        // JSON file — persists to a single JSON file
        cli.AddFileSystem(o => o.UseJsonFile("./data/files.json"));

        // SQLite — persists to a SQLite database
        cli.AddFileSystem(o => o.UseSqlite("./data/files.db"));

        // EF Core — uses any EF Core database provider
        var efOptions = new DbContextOptionsBuilder<FileStorageDbContext>()
            .UseSqlite("Data Source=./data/files-ef.db")
            .Options;
        cli.AddFileSystem(o => o.UseEfCore(efOptions));

        // Amazon S3
        cli.AddFileSystem(o => o.UseS3(s3 =>
        {
            s3.Bucket = "my-cli-files";
            s3.Region = "us-east-1";
            s3.Prefix = "uploads/";
            s3.AccessKeyId = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
            s3.SecretAccessKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        }));
    });
```

### Custom Provider

Implement `IFileStorageProvider` to add your own backend:

```csharp
public class MyProvider : IFileStorageProvider
{
    public string Name => "my-provider";
    public Task<List<FileEntry>> ListAsync(string path, CancellationToken ct = default) { /* ... */ }
    public Task<string> ReadFileAsync(string path, CancellationToken ct = default) { /* ... */ }
    public Task WriteFileAsync(string path, string content, CancellationToken ct = default) { /* ... */ }
    public Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default) { /* ... */ }
    public Task<FileStat> StatAsync(string path, CancellationToken ct = default) { /* ... */ }
    public Task MkdirAsync(string path, bool recursive = false, CancellationToken ct = default) { /* ... */ }
    public Task RemoveAsync(string path, bool recursive = false, CancellationToken ct = default) { /* ... */ }
    public Task CopyAsync(string src, string dest, CancellationToken ct = default) { /* ... */ }
    public Task MoveAsync(string src, string dest, CancellationToken ct = default) { /* ... */ }
    public Task<bool> ExistsAsync(string path, CancellationToken ct = default) { /* ... */ }
    public Task<Stream> GetDownloadStreamAsync(string path, CancellationToken ct = default) { /* ... */ }
    public Task UploadFileAsync(string path, byte[] content, CancellationToken ct = default) { /* ... */ }
}
```

Register it by setting `options.Provider` directly:

```csharp
cli.AddFileSystem(o => o.Provider = new MyProvider());
```

## Data Explorer

The Data Explorer plugin provides interactive access to data sources through a provider-based API. Each data source type (SQL, MongoDB, etc.) is a separate plugin implementing `IDataExplorerProvider`.

### API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/qcli/data-explorer/sources` | List registered data sources with metadata |
| POST | `/api/qcli/data-explorer/execute` | Execute a query against a named source |

### SQL Provider

```csharp
using Qodalis.Cli.Plugin.DataExplorer.Sql;
using Qodalis.Cli.Abstractions.DataExplorer;

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        cli.AddDataExplorerSql("Data Source=app.db", options =>
        {
            options.Name = "app-database";
            options.Description = "Application database";
            options.Language = DataExplorerLanguage.Sql;
            options.DefaultOutputFormat = DataExplorerOutputFormat.Table;
            options.Timeout = 30000;
            options.MaxRows = 1000;
            options.Templates =
            [
                new DataExplorerTemplate
                {
                    Name = "list_tables",
                    Query = "SELECT name FROM sqlite_master WHERE type='table'",
                    Description = "List all tables"
                },
            ];
        });
    });
```

### MongoDB Provider

```csharp
using Qodalis.Cli.Plugin.DataExplorer.Mongo;

cli.AddDataExplorerMongo("mongodb://localhost:27017", "myapp", options =>
{
    options.Name = "mongo-primary";
    options.Description = "Primary MongoDB database";
    options.Language = DataExplorerLanguage.Json;
    options.DefaultOutputFormat = DataExplorerOutputFormat.Json;
    options.Templates =
    [
        new DataExplorerTemplate
        {
            Name = "show_collections",
            Query = "show collections",
            Description = "List all collections"
        },
        new DataExplorerTemplate
        {
            Name = "find_users",
            Query = "db.users.find({})",
            Description = "Find all users"
        },
    ];
});
```

**Supported MongoDB operations:** `db.collection.find({...})`, `findOne`, `aggregate([...])`, `insertOne`, `insertMany`, `updateOne`, `updateMany`, `deleteOne`, `deleteMany`, `countDocuments`. Convenience commands: `show collections`, `show dbs`.

### Custom Provider

Implement `IDataExplorerProvider` to add your own data source:

```csharp
using Qodalis.Cli.Abstractions.DataExplorer;

public class MyProvider : IDataExplorerProvider
{
    public async Task<DataExplorerResult> ExecuteAsync(
        DataExplorerExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        // context.Query — the user's query string
        // context.Parameters — key-value parameters
        // context.Options — provider options (name, language, etc.)
        return new DataExplorerResult
        {
            Success = true,
            Source = context.Options.Name,
            Language = context.Options.Language,
            DefaultOutputFormat = context.Options.DefaultOutputFormat,
            Columns = new List<string> { "id", "name" },  // null for document-oriented results
            Rows = new List<object> { new object[] { 1, "Alice" } },
            RowCount = 1,
        };
    }
}
```

Register with DI (recommended) or as an instance:

```csharp
// DI — resolved from service container
cli.AddDataExplorerProvider<MyProvider>(options => { options.Name = "custom"; /* ... */ });

// Instance
cli.AddDataExplorerProvider(new MyProvider(), options => { options.Name = "custom"; /* ... */ });
```

The same provider class can be registered multiple times with different configurations (e.g., two databases with different names).

## AWS Cloud Services

The AWS plugin adds commands for managing AWS resources (S3, EC2, Lambda, CloudWatch, SNS, SQS, IAM, DynamoDB, ECS) directly from the CLI. It uses AWSSDK and supports the full credential chain.

```csharp
using Qodalis.Cli.Plugin.Aws;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddModule(new AwsModule());
});
```

### Authentication

The plugin resolves credentials in this order:

1. **CLI configure**: `aws configure set --key <KEY> --secret <SECRET> --region <REGION>`
2. **Environment variables**: `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_REGION`
3. **AWS profiles**: `aws configure set --profile <name>`
4. **IAM roles**: Automatic on EC2/ECS/Lambda

Verify connectivity with `aws status`.

### Available Commands

| Service | Commands |
|---------|----------|
| **configure** | `aws configure set`, `aws configure get`, `aws configure profiles` |
| **status** | `aws status` — STS GetCallerIdentity connectivity check |
| **S3** | `aws s3 ls`, `aws s3 cp`, `aws s3 rm`, `aws s3 mb`, `aws s3 rb`, `aws s3 presign` |
| **EC2** | `aws ec2 list`, `aws ec2 describe`, `aws ec2 start`, `aws ec2 stop`, `aws ec2 reboot`, `aws ec2 sg list` |
| **Lambda** | `aws lambda list`, `aws lambda invoke`, `aws lambda logs` |
| **CloudWatch** | `aws cloudwatch alarms`, `aws cloudwatch logs`, `aws cloudwatch metrics` |
| **SNS** | `aws sns topics`, `aws sns publish`, `aws sns subscriptions` |
| **SQS** | `aws sqs list`, `aws sqs send`, `aws sqs receive`, `aws sqs purge` |
| **IAM** | `aws iam users`, `aws iam roles`, `aws iam policies` |
| **DynamoDB** | `aws dynamodb tables`, `aws dynamodb describe`, `aws dynamodb scan`, `aws dynamodb query` |
| **ECS** | `aws ecs clusters`, `aws ecs services`, `aws ecs tasks` |

All commands support `--region` (`-r`) for region override and `--output` (`-o`) for format selection (`table`, `json`, `text`). Destructive commands support `--dry-run`.

See [`plugins/aws/README.md`](plugins/aws/README.md) for the full command reference.

### Users Plugin

The users plugin adds endpoints under `/api/v1/qcli` for authentication, users, sessions, groups and an audit log, with swappable storage (in-memory by default). It is the server side of the `@qodalis/cli-users` browser plugin's *server mode*.

```csharp
using Qodalis.Cli.Plugin.Users.Extensions;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddUsers(options => options.DefaultAdminPassword = "admin");
});

app.UseQodalisUsers();
```

See [`plugins/users/README.md`](plugins/users/README.md) for options, endpoints, API tokens, custom storage and external auth adapters.

## MCP endpoint

The server can expose the whole system to AI clients (Claude Code, Claude Desktop, Cursor, …) through the
[Model Context Protocol](https://modelcontextprotocol.io): CLI commands, the filesystem API, the data explorer,
jobs and tools contributed by plugins become MCP tools, each with the same permissions as a CLI user.
It works with or without the users plugin (which only supplies identities and API tokens).

### Enabling

MCP is off by default. Enable it on the CLI builder:

```csharp
builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddMcp(o =>
    {
        o.Tools = ["weather_*", "fs_*", "server_info"]; // optional allow-list (trailing * = prefix)
        o.Exclude = ["fs_delete"];                       // optional deny-list, applied after Tools
        o.Instructions = "Tools of the ACME ops server."; // optional, returned by initialize
        // o.Token = "...";                              // only WITHOUT an auth provider (see below)
    });
});
```

| Option | Default | Description |
|--------|---------|-------------|
| `Enabled` | `true` | Turn the endpoint off without removing the configuration |
| `Tools` | all | Allow-list of tool names; a trailing `*` matches a prefix |
| `Exclude` | none | Deny-list, same syntax, applied after `Tools` |
| `Token` | none | Static bearer token required when the server has **no** authentication provider |
| `Instructions` | none | Returned by `initialize` |

### Route and transport

`POST /api/qcli/mcp` — MCP *Streamable HTTP*, **stateless** (no `Mcp-Session-Id`) with **JSON responses only**
(no SSE stream). Each request carries one JSON-RPC 2.0 message: `initialize`, `ping`, `tools/list`,
`tools/call`; notifications get `202` with an empty body. JSON-RPC errors (`-32700` parse error, `-32600` invalid
request, `-32601` unknown method, `-32602` invalid params, unknown tool or invalid arguments) use HTTP 200.
`GET` and `DELETE` answer `405` (`Allow: POST`). When an `Origin` header is present it must be listed in
`cli.SetAllowedOrigins(...)` or be same-origin, otherwise `403` (DNS-rebinding protection). Both checks run
before authentication. No particular `Accept` header is required; responses are `application/json`. A JSON
array (batch) is an invalid request (`-32600`). `serverInfo.version` is the server package version.
Supported protocol versions: `2025-11-25`, `2025-06-18`, `2025-03-26` (the client's version is echoed when
supported, otherwise the newest is returned).

### Authentication

- **With the users plugin** (`ProtectServer`): the request is authenticated like every CLI route — a session
  token or an API token (`Authorization: Bearer qcli_pat_…`, see the users plugin) — and needs
  `commands:execute`. `401`/`403` are plain HTTP errors with an `{ "error": … }` body. `tools/list` only shows
  the tools the caller may call; calling any other tool is `Unknown tool`.
- **Without an authentication provider:** when `Token` is set, `Authorization: Bearer <Token>` is required
  (constant-time comparison, else `401`); when it is not set the endpoint is as open as `/execute`.

### Tools

Tools are collected on every request, so registrations, enabled features and the caller's permissions are
always current.

| Provider | Tools | Permission | Annotations |
|---|---|---|---|
| server | `server_info` (version, capabilities, enabled features, command names) | — | read-only |
| commands | one tool per command (below) | `commands:execute` + the processors' `RequiredPermissions` | — |
| filesystem (`cli.AddFileSystem(...)`) | `fs_list {path}`, `fs_stat {path}`, `fs_read {path, maxBytes?=262144}` | `files:read` | read-only |
| | `fs_write {path, content}`, `fs_mkdir {path}` | `files:write` | — |
| | `fs_delete {path, recursive?=false}` | `files:write` | destructive |
| data explorer (a provider registered) | `data_sources`, `data_schema {source}` | `data:query` | read-only |
| | `data_query {source, query, parameters?}` | `data:query` | — |
| jobs (`Qodalis.Cli.Plugin.Jobs`) | `jobs_list`, `jobs_get {id}`, `jobs_history {id, limit?=20}` | `jobs:read` | read-only |
| | `jobs_trigger`, `jobs_pause`, `jobs_resume`, `jobs_stop`, `jobs_cancel` `{id}` | `jobs:manage` | `jobs_stop`/`jobs_cancel` destructive |

Feature tools use the same services as the HTTP routes (the file storage provider and its allowed paths, the
data explorer executor with its timeouts and row limits, the job scheduler). Errors become `isError: true`
results. `fs_read` returns UTF-8 text (longer files end with `[truncated: N of M bytes]`; a binary file is the
error `binary file, N bytes`). Job tools accept a job id or a unique job name.

**Command tools.** Every root processor and sub-processor becomes a tool when it has no sub-processors, declares
parameters, or takes a value (`ValueRequired = true`, or `AcceptsRawInput = true` for an optional positional input). The name is the command path joined with `_`
(`weather`, `weather_current`), lower-cased, other characters than `[a-z0-9_-]` replaced by `-`, at most 64
characters (on a collision the first one wins and a warning is logged). The description is the processor's
(fallback `Run '<command path>'`). The input schema has one property per parameter (`string`, `number`,
`boolean`, `array` of strings; other types → `string`; with `description`, `default` and `required`) plus `value`
(the positional input; required when `ValueRequired`, optional with `AcceptsRawInput`). A call builds the same command the CLI would send and runs
it through the same executor as `/execute`; the outputs are rendered as text (tables as `A | B` rows, lists one
item per line, JSON pretty-printed, key-values as `key: value`), `isError` is `exitCode != 0`, and a single JSON
object output is also returned as `structuredContent`.

### Adding tools from a plugin

Implement `IMcpToolProvider` and register it with `services.AddMcpToolProvider<T>()`:

```csharp
using System.Text.Json.Nodes;
using Qodalis.Cli.Mcp;

public sealed class InventoryMcpTools(IInventory inventory) : IMcpToolProvider
{
    public string? Feature => "inventory"; // reported by server_info

    public Task<IReadOnlyList<McpToolDefinition>> GetToolsAsync(McpToolContext context, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<McpToolDefinition>>(
        [
            new McpToolDefinition
            {
                Name = "inventory_count",
                Description = "Count the items of a SKU",
                InputSchema = McpSchema.Object(new JsonObject { ["sku"] = McpSchema.Property("string", "The SKU") }, "sku"),
                RequiredPermissions = ["inventory:read"],      // checked for list and call when the server is protected
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = async (args, ctx, token) =>
                    McpToolResult.Ok($"{await inventory.CountAsync(McpSchema.GetString(args, "sku")!, token)} items"),
            },
        ]);
}

cli.Services.AddMcpToolProvider<InventoryMcpTools>();
```

Arguments are validated against `InputSchema` before `CallAsync` runs; exceptions become `isError: true` results.
On a name collision built-in feature tools win, then plugin providers in registration order, then command tools.

### Using the MCP endpoint from an MCP client

These steps connect [Claude Code](https://docs.claude.com/en/docs/claude-code) to a server running on
`http://localhost:8046` (the demo server: `cd demo && dotnet run`; it enables MCP and seeds `root` / `root`).

#### 1. Enable MCP on the server

```csharp
builder.Services.AddControllers().AddCli(cli =>
{
    // ... processors, cli.AddFileSystem(...), data explorer, jobs ...
    cli.AddMcp();                                   // POST /api/qcli/mcp
    cli.AddUsers(o => o.DefaultAdminPassword = "root"); // optional: users, sessions, API tokens
});

var app = builder.Build();
app.UseWebSockets();
app.UseQodalisUsers();   // with the users plugin
app.MapControllers();
app.UseCli();
```

#### 2. Get a credential

**With the users plugin** sign in, then create an API token with the session token (the API token is shown only
once; it expires after 90 days unless `expiresInDays` says otherwise):

```bash
SESSION=$(curl -s -X POST http://localhost:8046/api/v1/qcli/sessions \
  -H "Content-Type: application/json" \
  -d '{"username":"root","password":"root"}' | jq -r .token)

TOKEN=$(curl -s -X POST http://localhost:8046/api/v1/qcli/tokens \
  -H "Authorization: Bearer $SESSION" \
  -H "Content-Type: application/json" \
  -d '{"name":"claude-code","expiresInDays":90}' | jq -r .token)

echo "$TOKEN"   # qcli_pat_...
```

Add `"permissions": ["commands:execute", "files:read"]` to the token body to limit what the token may do.
In the browser CLI (`@qodalis/cli-users` in server mode) the same is `token create claude-code`.

**Without the users plugin** choose the token yourself and configure it on the server; use it as `$TOKEN` below
(without `Token` the endpoint is open and needs no header):

```csharp
cli.AddMcp(o => o.Token = builder.Configuration["Mcp:Token"]);
```

#### 3. Connect Claude Code

**(a) Directly over HTTP with the API token:**

```bash
claude mcp add --transport http qcli http://localhost:8046/api/qcli/mcp --header "Authorization: Bearer <token>"
```

**(b) Through the [`@qodalis/mcp-cli`](https://github.com/qodalis-solutions/mcp-cli) bridge**, a local MCP server
configured with environment variables:

```bash
claude mcp add qcli -e QCLI_URL=http://localhost:8046 -e QCLI_TOKEN=<token> -- npx -y @qodalis/mcp-cli
```

Use `QCLI_USER` / `QCLI_PASSWORD` instead of `QCLI_TOKEN` to sign in with a user, and `QCLI_READ_ONLY=true` to
expose only read-only tools.

Then run `claude mcp list` (or `/mcp` inside Claude Code) to check that `qcli` is connected, and ask Claude
to use it ("list the qcli tools", "run echo hello through qcli").

#### 4. Check the endpoint with curl

Each request is one JSON-RPC message; responses are plain JSON:

```bash
curl -s -X POST http://localhost:8046/api/qcli/mcp \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"1.0"}}}'

curl -s -X POST http://localhost:8046/api/qcli/mcp \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' | jq -r '.result.tools[].name'

curl -s -X POST http://localhost:8046/api/qcli/mcp \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"value":"hello from MCP"}}}'
```

Expected: `initialize` returns `{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-06-18",...,"serverInfo":{"name":"qodalis-cli-server",...}}}`,
`tools/list` prints the tool names (`server_info`, `fs_list`, …, `echo`, `math_add`, …) and `tools/call` returns
`{"jsonrpc":"2.0","id":3,"result":{"content":[{"type":"text","text":"hello from MCP"}],"isError":false}}`.

#### Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `401 {"error":"unauthorized"}` / `invalid or expired token` | Missing `Authorization` header, or the token is unknown, expired or revoked, or its user is disabled. Create a new token (`GET /api/v1/qcli/tokens` lists yours). Without the users plugin: the header must match `Token`. |
| `403 {"error":"forbidden: origin not allowed"}` | The request has an `Origin` header that is neither same-origin nor in `cli.SetAllowedOrigins(...)`. Command-line clients send no `Origin`; browsers do. |
| `403 {"error":"permission denied: commands:execute"}` | The user (or the token's scope) lacks `commands:execute`, which the endpoint needs. |
| `403 {"error":"password change required"}` | The user must change the password first (sign in and `PUT /users/{name}/password`). |
| A tool is missing from `tools/list` | The caller lacks its permission (e.g. `files:read` for `fs_read`, `data:query`, `jobs:read`/`jobs:manage`, or a processor's `RequiredPermissions`; a token scope narrows them further), the feature is not enabled (`AddFileSystem`, a data source, the jobs plugin), or the `Tools` / `Exclude` lists filter it out. Calling it gives `Unknown tool: <name>`. |
| `TOKEN` is `null` | The token request failed: `409` when a token with that name already exists (use another name, or revoke it with `DELETE /api/v1/qcli/tokens/{id}`), `400` at 20 tokens, or the sign-in failed. Run the `curl` without `\| jq -r .token` to see the error. |
| `405` | Only `POST` is supported (no SSE stream, no sessions). |
| `404` | MCP is not enabled (`cli.AddMcp()`). |
| `claude mcp list` shows `qcli` as failed | Check the URL and run the curl `initialize` above with the same token. |

## Docker

```bash
docker run -p 8046:8046 ghcr.io/qodalis-solutions/cli-server-dotnet
```

## Demo

```bash
cd demo
dotnet run
# Server starts on http://localhost:8046
```

## Project Structure

```
src/
  Qodalis.Cli.Abstractions/   # Interfaces and base classes (NuGet package)
    ICliCommandProcessor.cs        # Core processor interface
    ICliModule.cs                  # Module interface
    CliCommandAuthor.cs            # Author metadata
    CliCommandParameterDescriptor.cs  # Parameter declaration
    CommandParameterType.cs        # Parameter type enum
  Qodalis.Cli/                 # ASP.NET Core integration (NuGet package)
    CliCommandProcessor.cs         # Processor base class with defaults
    CliModule.cs                   # Module base class with defaults
    Controllers/
      CliController.cs             # REST API (/api/v1/qcli)
      CliVersionController.cs      # Version discovery (/api/qcli/versions)
      FileSystemController.cs      # Filesystem API (/api/qcli/fs)
      DataExplorerController.cs    # Data Explorer API (/api/qcli/data-explorer)
    Extensions/
      CliBuilder.cs                # Fluent registration API (AddProcessor, AddModule)
      MvcBuilderExtensions.cs      # AddCli() extension method
      WebApplicationExtensions.cs  # UseCli() extension method
    Services/
      CliCommandRegistry.cs        # Processor registry and lookup
      CliCommandExecutorService.cs # Command execution pipeline
      CliResponseBuilder.cs        # Structured output builder
      CliEventSocketManager.cs     # WebSocket event broadcasting
      CliLogSocketManager.cs       # WebSocket log streaming
      ShellSessionManager.cs       # Interactive PTY shell sessions
    Models/
      CliServerResponse.cs         # Response wrapper (exitCode + outputs)
      CliServerOutput.cs           # Output types (text, table, list, json, key-value)
      CliServerCommandDescriptor.cs  # Command metadata for /commands endpoint
  Qodalis.Cli.Server/          # Standalone server with Swagger UI
plugins/
  filesystem/                  # Core file storage abstraction (IFileStorageProvider, InMemory, OS)
  filesystem-json/             # JSON file persistence provider
  filesystem-sqlite/           # SQLite persistence provider
  filesystem-efcore/           # EF Core persistence provider
  filesystem-s3/               # Amazon S3 storage provider
  data-explorer-sql/           # SQLite data explorer provider
  data-explorer-mongo/         # MongoDB data explorer provider
  data-explorer-postgres/      # PostgreSQL data explorer provider
  data-explorer-mysql/         # MySQL data explorer provider
  data-explorer-mssql/         # MS SQL Server data explorer provider
  data-explorer-redis/         # Redis data explorer provider
  data-explorer-elasticsearch/ # Elasticsearch data explorer provider
  users/                       # Users plugin (auth, sessions, groups, audit)
  weather/                     # Weather module (example plugin)
  admin/                       # Admin dashboard and monitoring
  jobs/                        # Job scheduling and management
  jobs-efcore/                 # EF Core persistence for jobs
  aws/                         # AWS cloud resource management
demo/                          # Demo app with sample processors
tests/                         # Unit tests (xUnit)
```

## License

MIT
