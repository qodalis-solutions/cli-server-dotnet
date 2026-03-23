# Qodalis.Cli.Plugin.FileSystem.Sqlite

SQLite persistence provider for [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet) file storage. Stores the virtual filesystem in a lightweight SQLite database file.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.FileSystem.Sqlite
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Plugin.FileSystem.Sqlite;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddFileSystem(o => o.UseSqlite("./data/files.db"));
});
```

Pass `":memory:"` for an in-memory SQLite database (no persistence across restarts).

## Dependencies

- `Qodalis.Cli.Plugin.FileSystem`
- `Microsoft.Data.Sqlite` 8.0

## License

MIT
