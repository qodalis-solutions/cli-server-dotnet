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

## License

MIT
