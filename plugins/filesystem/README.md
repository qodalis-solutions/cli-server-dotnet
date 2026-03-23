# Qodalis.Cli.Plugin.FileSystem

Pluggable file storage abstraction for [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet). Defines the `IFileStorageProvider` interface and ships two built-in providers: in-memory and OS filesystem. Additional providers are available as separate packages.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.FileSystem
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.FileSystem;

builder.Services.AddControllers().AddCli(cli =>
{
    // In-memory provider (no persistence)
    cli.AddFileSystem(o => o.UseInMemory());

    // OS filesystem provider with allowed paths
    cli.AddFileSystem(o => o.UseOsFileSystem(os =>
    {
        os.AllowedPaths.Add("/tmp");
        os.AllowedPaths.Add("/data");
    }));
});
```

## Providers

| Provider | Package | Description |
|---|---|---|
| In-Memory | *(built-in)* | Volatile storage for testing and ephemeral use |
| OS Filesystem | *(built-in)* | Local disk with configurable path allowlisting |
| EF Core | `Qodalis.Cli.Plugin.FileSystem.EfCore` | Any EF Core-compatible database |
| JSON File | `Qodalis.Cli.Plugin.FileSystem.Json` | Single JSON file on disk |
| SQLite | `Qodalis.Cli.Plugin.FileSystem.Sqlite` | SQLite database file |
| Amazon S3 | `Qodalis.Cli.Plugin.FileSystem.S3` | S3 or S3-compatible storage |

## IFileStorageProvider Interface

All providers implement the `IFileStorageProvider` interface:

| Method | Description |
|---|---|
| `ListAsync(path)` | List files and directories |
| `ReadFileAsync(path)` | Read file content as text |
| `WriteFileAsync(path, content)` | Write text or binary content |
| `StatAsync(path)` | Get file/directory metadata |
| `MkdirAsync(path, recursive)` | Create a directory |
| `RemoveAsync(path, recursive)` | Delete a file or directory |
| `CopyAsync(src, dest)` | Copy a file or directory |
| `MoveAsync(src, dest)` | Move/rename a file or directory |
| `ExistsAsync(path)` | Check if a path exists |
| `GetDownloadStreamAsync(path)` | Open a download stream |
| `UploadFileAsync(path, content)` | Upload binary content |

## Custom Provider

Implement `IFileStorageProvider` and assign it to the options:

```csharp
cli.AddFileSystem(o => o.Provider = new MyCustomProvider());
```

## License

MIT
