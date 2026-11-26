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
same on the three servers). Its schema is versioned with the [databases plugin](../databases)'s runner (source
`qcli-filesystem`, history in `QcliMigrations` next to it): the first start after an upgrade renames the `files` table
of 1.0.0-beta.29 and older and its columns, keeping the files. `o.UseSqlite(path, s => s.LegacyTableNames = true)`
keeps the old names; `TablePrefix` and `Tables["Files"]` set others. To show the schema in the dashboard:
`d.AddDatabase("files", db => db.UseConnection(_ => provider.CreateMigrationConnection())).AddSource("files", provider.MigrationSource)`.

## Dependencies

- `Qodalis.Cli.Plugin.FileSystem`
- `Qodalis.Cli.Plugin.Databases`
- `Microsoft.Data.Sqlite` 10.0

## Terminal commands

None of its own: the storage is reached through the [filesystem plugin](../filesystem/README.md#terminal-commands)
(`scp` in the web CLI).

## Install from the dashboard or in code

The package declares an entry point (`SqlitePluginEntry`, plugin id `filesystem-sqlite`), so it can be installed from the dashboard's
marketplace (the admin plugin's server plugins, loaded at the next start) or added in code through the same entry:

```csharp
cli.AddPlugin<SqlitePluginEntry>(new { readOnly = false });
```

Its settings (config provider `filesystem-sqlite` unless noted; code options are their defaults, saved settings override them):
`database` (relative to the plugin's data folder, default `files.db`), `tablePrefix`, `readOnly` (default true). A required setting without a value lists the plugin as `needs-configuration` with `missing`; see
[Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard).

## License

MIT
