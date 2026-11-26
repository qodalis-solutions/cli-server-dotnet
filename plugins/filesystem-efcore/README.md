# Qodalis.Cli.Plugin.FileSystem.EfCore

Entity Framework Core persistence provider for [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet) file storage. Stores the virtual filesystem in any EF Core-compatible database (SQL Server, PostgreSQL, MySQL, SQLite, etc.).

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.FileSystem.EfCore
```

## Quick Start

```csharp
using Microsoft.EntityFrameworkCore;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Plugin.FileSystem.EfCore;

var efOptions = new DbContextOptionsBuilder<FileStorageDbContext>()
    .UseSqlServer("Server=localhost;Database=MyDb;...")
    .Options;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddFileSystem(o => o.UseEfCore(efOptions));
});
```

The `FileStorageDbContext` schema is created automatically on first use via `Database.EnsureCreated()`.

## Table names (1.0.0-beta.30)

The table is `QcliFiles` (cli-server-dotnet#99). The first start after an upgrade renames the `files` table of
1.0.0-beta.29 and older, keeping the files; `o.UseEfCore(efOptions, t => t.LegacyTableNames = true)` keeps the old
name, `TablePrefix`, `Tables["Files"]` and `Schema` set others.

## Dependencies

- `Qodalis.Cli.Plugin.FileSystem`
- `Microsoft.EntityFrameworkCore` 8.0
- `Microsoft.EntityFrameworkCore.Relational` 8.0

## Terminal commands

None of its own: the storage is reached through the [filesystem plugin](../filesystem/README.md#terminal-commands)
(`scp` in the web CLI).

## Install from the dashboard or in code

The package declares an entry point (`FileSystemEfCorePluginEntry`, plugin id `filesystem-efcore`), so it can be installed from the dashboard's
marketplace (the admin plugin's server plugins, loaded at the next start) or added in code through the same entry:

```csharp
cli.AddPlugin<FileSystemEfCorePluginEntry>(new { connectionString = "files.db" });
```

Its settings (config provider `filesystem-efcore` unless noted; code options are their defaults, saved settings override them):
`dialect` (sqlite, postgres, mssql), `connectionString` (required secret; for SQLite a file in the plugin's data folder), `tablePrefix`, `readOnly` (default true); kept in `plugin-settings.json` and read at start. A required setting without a value lists the plugin as `needs-configuration` with `missing`; see
[Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard).

## License

MIT
