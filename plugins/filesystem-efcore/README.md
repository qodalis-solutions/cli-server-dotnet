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

## Dependencies

- `Qodalis.Cli.Plugin.FileSystem`
- `Microsoft.EntityFrameworkCore` 8.0
- `Microsoft.EntityFrameworkCore.Relational` 8.0

## License

MIT
