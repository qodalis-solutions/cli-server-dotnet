# Qodalis CLI Server (.NET)

A .NET CLI server framework for the [Qodalis CLI](https://github.com/qodalis-solutions/angular-web-cli) ecosystem. Built with ASP.NET Core.

## Installation

```bash
dotnet add package Qodalis.Cli.Abstractions
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
        cli.AddProcessor<MyCommandProcessor>();
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

## API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/cli/version` | Server version |
| GET | `/api/cli/commands` | List available commands |
| POST | `/api/cli/execute` | Execute a command |
| WS | `/ws/cli/events` | WebSocket event channel |

## Creating Command Processors

```csharp
using Qodalis.Cli.Abstractions;

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

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var useUtc = command.Args.ContainsKey("utc");
        var format = command.Args.TryGetValue("format", out var fmt) ? fmt.ToString() : "yyyy-MM-dd HH:mm:ss";
        var now = useUtc ? DateTime.UtcNow : DateTime.Now;
        var label = useUtc ? "UTC" : "Local";

        return Task.FromResult($"{label}: {now.ToString(format)}");
    }
}
```

### Sub-commands

```csharp
public class MathCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "math";
    public override string Description { get; set; } = "Performs basic math operations";
    public override bool? AllowUnlistedCommands { get; set; } = false;

    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; } = new ICliCommandProcessor[]
    {
        new MathAddProcessor(),
        new MathMultiplyProcessor(),
    };

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult("Usage: math add|multiply --a <number> --b <number>");
    }
}
```

## Docker

```bash
docker run -p 8046:8046 qodalis/cli-server-dotnet
```

The Docker image runs a demo server with sample processors (echo, status, time, hello, math).

## Demo

```bash
cd demo
dotnet run
# Server starts on http://localhost:8046
```

## Project Structure

```
src/
  Qodalis.Cli.Abstractions/   # Interfaces: ICliCommandProcessor, parameter descriptors
  Qodalis.Cli/                 # ASP.NET Core integration: controller, DI, WebSocket
  Qodalis.Cli.Server/          # Standalone server with built-in processors
demo/                          # Demo app with 5 sample processors
```

## License

MIT
