# Qodalis CLI Server (.NET)

A .NET CLI server framework for the [Qodalis CLI](https://github.com/qodalis-solutions/angular-web-cli) ecosystem. Build custom server-side commands that integrate with the Qodalis web terminal.

## Packages

| Package | Purpose | NuGet |
|---------|---------|-------|
| `Qodalis.Cli.Abstractions` | Interfaces and base classes for writing command processors | [![NuGet](https://img.shields.io/nuget/v/Qodalis.Cli.Abstractions)](https://www.nuget.org/packages/Qodalis.Cli.Abstractions) |
| `Qodalis.Cli` | ASP.NET Core integration (controllers, DI, WebSocket) | [![NuGet](https://img.shields.io/nuget/v/Qodalis.Cli)](https://www.nuget.org/packages/Qodalis.Cli) |

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

The `CliBuilder` supports three registration approaches:

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
        cli.AddProcessorsFromAssembly(typeof(MyPlugin).Assembly);
    });
```

Assembly scanning registers every class implementing `ICliCommandProcessor` found in the given assembly.

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
    ICliCommandProcessor.cs        # Core interface
    CliCommandAuthor.cs            # Author metadata
    CliCommandParameterDescriptor.cs  # Parameter declaration
    CommandParameterType.cs        # Parameter type enum
  Qodalis.Cli/                 # ASP.NET Core integration (NuGet package)
    CliCommandProcessor.cs         # Base class with defaults
    Controllers/
      CliController.cs             # V1 REST API (/api/v1/cli)
      CliControllerV2.cs           # V2 REST API (/api/v2/cli)
      CliVersionController.cs      # Version discovery (/api/cli/versions)
    Extensions/
      CliBuilder.cs                # Fluent registration API
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
demo/                          # Demo app with sample processors
tests/                         # Unit tests (xUnit)
```

## License

MIT
