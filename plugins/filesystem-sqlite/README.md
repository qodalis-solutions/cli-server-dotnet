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

## Table names (1.0.0-beta.30)

The table is `QcliFiles` with PascalCase columns (`CreatedAt`, `ModifiedAt`, `ParentPath`; cli-server-dotnet#99, the
same on the three servers). Its schema is versioned with the [migrations plugin](../migrations)'s runner (source
`qcli-filesystem`, history in `QcliMigrations` next to it): the first start after an upgrade renames the `files` table
of 1.0.0-beta.29 and older and its columns, keeping the files. `o.UseSqlite(path, s => s.LegacyTableNames = true)`
keeps the old names; `TablePrefix` and `Tables["Files"]` set others. To show the schema in the dashboard:
`m.AddDatabase("files", db => db.UseConnection(_ => provider.CreateMigrationConnection())).AddSource("files", provider.MigrationSource)`.

## Dependencies

- `Qodalis.Cli.Plugin.FileSystem`
- `Qodalis.Cli.Plugin.Migrations`
- `Microsoft.Data.Sqlite` 10.0

## License

MIT
