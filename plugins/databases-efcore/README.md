# Qodalis.Cli.Plugin.Databases.EfCore

EF Core for the [databases plugin](../databases): a `DbContext`'s migrations shown, applied and rolled back from the
admin dashboard, the terminal and MCP, and the schemas of the CLI's EF Core stores as migration sources.

```bash
dotnet add package Qodalis.Cli.Plugin.Databases.EfCore
```

## A DbContext's migrations

```csharp
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddControllers().AddCli(cli => cli
    .AddDatabases(d => d.AddEfCore<AppDbContext>("main", db => db.ApplyMigrationsOnStartup())));
```

This replaces `await db.Database.MigrateAsync()` at startup: pending migrations apply before the server takes
requests (a failure stops it), under the plugin's lock, so two services starting together (an API and a worker)
apply once. The context comes from `IDbContextFactory<TContext>`, or from a scope when it is registered with
`AddDbContext`; the database is registered on the context's connection string unless `UseConnection` sets another.

- Status from `GetMigrations()` and `GetAppliedMigrationsAsync()`; EF keeps its history in `__EFMigrationsHistory`, so
  `dotnet ef database update` and CI keep working. The plugin records the runs in `QcliMigrationRuns`.
- Up and down run one migration at a time with `IMigrator.MigrateAsync(target)`, so a run shows each migration's
  duration and stops at the one that failed. Down runs the migrations' `Down` methods.
- Dry runs show `GenerateScript(from, to)`.
- The source is named `ef` (`AddEfCore<T>("main", sourceName: "app")` names it).

## The CLI's EF Core stores

The stores (users-efcore, jobs-efcore, audit-efcore, public-api-efcore, filesystem-efcore) name their tables `Qcli` +
PascalCase (`QcliAuditEntries`, ...; cli-server-dotnet#99) and rename the previous release's tables on first use,
keeping their rows. With the databases plugin, each store's schema can also be a source of a database, so the
dashboard shows it and startup applies it:

```csharp
cli.AddEfCoreAuditStore(o => o.UseNpgsql(cs));
cli.AddEfCoreJobStorage(o => o.UseNpgsql(cs));
cli.AddDatabases(d => d
    .AddAuditStoreSchema("cli", db => db.ApplyMigrationsOnStartup())
    .AddJobStorageSchema("cli"));
```

`AddApiTokenStoreSchema` (users-efcore) and `AddPublicApiUsageSchema` (public-api-efcore) work the same way.

Store authors use `QcliEfTables` (`options.UseQcliTables(...)`: table names carried in the context's options, so two
stores with different names in one process each get their own model), `QcliEfStoreSchema.EnsureAsync` (renames the
legacy tables and their keys and indexes, then creates what is missing) and `QcliEfStoreRenames<TContext>`.
