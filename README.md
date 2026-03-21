# Qodalis CLI Server (.NET)

A .NET CLI server framework for the [Qodalis CLI](https://github.com/qodalis-solutions/web-cli) ecosystem. Build custom server-side commands that integrate with the Qodalis web terminal.

## Packages

| Package | Purpose | NuGet |
|---------|---------|-------|
| `Qodalis.Cli.Abstractions` | Interfaces and base classes for writing command processors | [![NuGet](https://img.shields.io/nuget/v/Qodalis.Cli.Abstractions)](https://www.nuget.org/packages/Qodalis.Cli.Abstractions) |
| `Qodalis.Cli` | ASP.NET Core integration (controllers, DI, WebSocket) | [![NuGet](https://img.shields.io/nuget/v/Qodalis.Cli)](https://www.nuget.org/packages/Qodalis.Cli) |

### File Storage Plugins

| Package | Storage Backend | Dependencies |
|---------|----------------|--------------|
| `Qodalis.Cli.Plugin.FileSystem` | Core abstraction + InMemory + OS providers | — |
| `Qodalis.Cli.Plugin.FileSystem.Json` | JSON file persistence | — |
| `Qodalis.Cli.Plugin.FileSystem.Sqlite` | SQLite database | Microsoft.Data.Sqlite |
| `Qodalis.Cli.Plugin.FileSystem.EfCore` | Entity Framework Core (any DB) | Microsoft.EntityFrameworkCore |
| `Qodalis.Cli.Plugin.FileSystem.S3` | Amazon S3 | AWSSDK.S3 |

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

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
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
| GET | `/api/cli/versions` | Version discovery (supported versions, preferred version) |
| GET | `/api/v1/cli/version` | V1 server version |
| GET | `/api/v1/cli/commands` | V1 commands (all processors) |
| POST | `/api/v1/cli/execute` | V1 execute |
| GET | `/api/v2/cli/version` | V2 server version |
| GET | `/api/v2/cli/commands` | V2 commands (only `ApiVersion >= 2`) |
| POST | `/api/v2/cli/execute` | V2 execute |
| WS | `/ws/cli/events` | WebSocket events (also `/ws/v1/cli/events`, `/ws/v2/cli/events`) |

The Qodalis CLI client auto-negotiates the highest mutually supported version via the `/api/cli/versions` discovery endpoint.

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
| `time` | Current date/time with UTC and format options |
| `hello` | Greeting with optional name parameter |
| `math` | Math operations with `add` and `multiply` sub-commands |
| `system` | Detailed system information |
| `http` | HTTP request operations |
| `hash` | Hash computation (MD5, SHA1, SHA256, SHA512) |
| `base64` | Base64 encode/decode |
| `uuid` | UUID generation |

## File Storage

The server includes a pluggable file storage system exposed at `/api/cli/fs/*`. Enable it with `AddFileSystem()` and choose a storage backend.

### Filesystem API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/cli/fs/ls?path=/` | List directory contents |
| GET | `/api/cli/fs/cat?path=/file.txt` | Read file content |
| GET | `/api/cli/fs/stat?path=/file.txt` | File/directory metadata |
| GET | `/api/cli/fs/download?path=/file.txt` | Download file |
| POST | `/api/cli/fs/upload` | Upload file (multipart) |
| POST | `/api/cli/fs/mkdir` | Create directory |
| DELETE | `/api/cli/fs/rm?path=/file.txt` | Delete file or directory |

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
      CliController.cs             # V1 REST API (/api/v1/cli)
      CliControllerV2.cs           # V2 REST API (/api/v2/cli)
      CliVersionController.cs      # Version discovery (/api/cli/versions)
    Extensions/
      CliBuilder.cs                # Fluent registration API (AddProcessor, AddModule)
      MvcBuilderExtensions.cs      # AddCli() extension method
      WebApplicationExtensions.cs  # UseCli() extension method
    Services/
      CliCommandRegistry.cs        # Processor registry and lookup
      CliCommandExecutorService.cs # Command execution pipeline
      CliResponseBuilder.cs        # Structured output builder
      CliEventSocketManager.cs     # WebSocket event broadcasting
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
  weather/                     # Weather module (example plugin)
demo/                          # Demo app with sample processors
tests/                         # Unit tests (xUnit)
```

## License

MIT
