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

Only paths under an allowed root are reachable. The roots themselves cannot be deleted, moved or renamed (`403`, `FileStoragePermissionError`); their contents can. The comparison is made after path normalization and symlink resolution, so `/tmp/`, `/tmp/x/..` or a symlink pointing at a root are refused as well.

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

## Events

`AddCli` wraps the provider in `CliEventFileStorageProvider`, which publishes `fs:changed` on the server's event bus
after every change: `{ action, path, newPath?, actor? }` with action `upload`, `mkdir`, `delete`, `write`, `move` or
`copy` (`newPath` for move and copy). The events WebSocket sends it to sockets with `files:read`. The filesystem API, the
MCP `fs_*` tools and any other caller of the registered `IFileStorageProvider` publish it, whatever the provider.

## Custom Provider

Implement `IFileStorageProvider` and assign it to the options:

```csharp
cli.AddFileSystem(o => o.Provider = new MyCustomProvider());
```

## Terminal commands

No server-side command: the web CLI drives `/api/qcli/fs/*` (`files:read` for reading, `files:write` for changes on a
protected server) through [`@qodalis/cli-scp`](https://www.npmjs.com/package/@qodalis/cli-scp):

| Command | Permission | Does |
|---|---|---|
| `scp ls <server> <path>`, `scp cat <server> <path>`, `scp stat <server> <path>` | `files:read` | Lists a directory, prints a file, shows its details |
| `scp download <server> <remote> [local]` (`get`), `scp <server>:<remote> [local]` | `files:read` | Downloads into the virtual file system (`--save`: through the browser) |
| `scp upload <server> <local> <remote>` (`put`), `scp <local> <server>:<remote>` | `files:write` | Uploads from the virtual file system (`--local`: a file you pick) |
| `scp mkdir <server> <path>`, `scp rm <server> <path>` | `files:write` | Creates a directory, deletes a file |

The `ls`, `cat`, `cd`, ... commands of `@qodalis/cli-files` work on the browser's own file system, not the server's.
Allowed paths are a setting too: `config get filesystem` (core `config` command).

## License

MIT
