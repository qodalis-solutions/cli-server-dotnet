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

## Terminal commands

None of its own: the storage is reached through the [filesystem plugin](../filesystem/README.md#terminal-commands)
(`scp` in the web CLI).

## Install from the dashboard or in code

The package declares an entry point (`JsonFilePluginEntry`, plugin id `filesystem-json`), so it can be installed from the dashboard's
marketplace (the admin plugin's server plugins, loaded at the next start) or added in code through the same entry:

```csharp
cli.AddPlugin<JsonFilePluginEntry>(new { readOnly = false });
```

Its settings (config provider `filesystem-json` unless noted; code options are their defaults, saved settings override them):
`file` (relative to the plugin's data folder, default `files.json`), `readOnly` (default true); refused when the storage is already set. A required setting without a value lists the plugin as `needs-configuration` with `missing`; see
[Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard).

## License

MIT
