# Qodalis.Cli.Plugin.FileSystem.Json

JSON file persistence provider for [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet) file storage. Stores the entire virtual filesystem tree as a single JSON file on disk.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.FileSystem.Json
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Plugin.FileSystem.Json;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddFileSystem(o => o.UseJsonFile("./data/files.json"));
});
```

The JSON file is loaded on startup and saved after each write operation. If the file does not exist, an empty filesystem is created.

## Dependencies

- `Qodalis.Cli.Plugin.FileSystem`

## License

MIT
