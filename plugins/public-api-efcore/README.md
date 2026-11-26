# Qodalis.Cli.Plugin.PublicApi.EfCore

Entity Framework Core store for the [public-api plugin](../public-api/README.md)'s usage counters. Keeps the request
counters (per token and UTC day or minute) in a database, so the quotas hold across every process that serves the API
and the usage shown on token views survives restarts.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.PublicApi.EfCore
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.PublicApi;
using Qodalis.Cli.Plugin.PublicApi.EfCore;

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        cli.AddUsers();
        cli.AddPublicApi(o => o.UseQuotaResolver<PlanQuotaResolver>());

        // Any EF Core provider works
        cli.AddEfCorePublicApiUsageStore(o => o.UseNpgsql(connectionString));
    });
```

The `QcliApiUsage` table is created on first use, also inside a database that already has other tables.

## Table names (1.0.0-beta.30)

The table is `QcliApiUsage` (cli-server-dotnet#99: `Qcli` + PascalCase, the same on the three servers). On the first start
after an upgrade the store renames the `qcli_api_usage` of 1.0.0-beta.29 to the new name, with its keys and indexes, keeping the rows.
The store's options (`AddEfCorePublicApiUsageStore(..., o => ...)`) set the names: `TablePrefix` (default `Qcli`), `Tables["…"]` for one table, `Schema`, and
`LegacyTableNames = true` to keep the old names.

A host that maps the table in its own model (`modelBuilder.ApplyPublicApiUsageModel()`) gets the new name there too: its next
`dotnet ef migrations add` generates the `RenameTable`, or it keeps the old name with `ApplyPublicApiUsageModel("qcli_api_usage")` and `LegacyTableNames = true`.

With the [databases plugin](../databases-efcore), the schema is a source of a database, so the dashboard shows it and startup applies it.

```csharp
cli.AddDatabases(d => d.AddPublicApiUsageSchema("cli", db => db.ApplyMigrationsOnStartup()));
```

## Host migrations

A host that manages its database with EF Core migrations creates the table in its own migrations and turns the
store's table creation off:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ApplyPublicApiUsageModel();
}

cli.AddEfCorePublicApiUsageStore(o => o.UseNpgsql(connectionString), o => o.EnsureCreated = false);
```

| Column | Type | |
|---|---|---|
| `TokenId` | string, 64 | primary key (with `Window`) |
| `UserId` | string, 128 | index (with `Window`); the same limit as `QcliApiTokens.UserId`: user ids (e.g. from an external adapter) must fit |
| `Window` | string, 16 | `yyyy-MM-dd` (UTC day) or `yyyy-MM-ddTHH:mm` (UTC minute) |
| `Count` | bigint | requests counted in the window |

## Several processes

Admission is increment-then-check-then-compensate: a request increments its token's day and minute counters, reads
the account's sums and, over a limit, takes its counts back and is refused. No more than the limit is ever admitted;
under contention a request may be refused while the account is just below it. Day rows are kept
`PublicApiOptions.UsageRetentionDays` (31) days, minute rows two minutes; the guard prunes at most once an hour, with
one `DELETE` that scans the table (fine at this size: about 31 rows per token plus two minutes of minute rows).

Tokens themselves must be created and revoked from one service only (the users plugin checks token names and the
20-token limit in the process that creates them); every service may validate them over the shared token store.

## Terminal commands

None of its own: it stores the usage counters of the [public-api plugin](../public-api/README.md#terminal-commands),
shown by that plugin's `api usage`. Its migrations show in `databases runs` when the
[databases plugin](../databases/README.md#terminal-commands) runs them (`AddPublicApiUsageSchema`).

## Install from the dashboard or in code

The package declares an entry point (`PublicApiEfCorePluginEntry`, plugin id `public-api-efcore`), so it can be installed from the dashboard's
marketplace (the admin plugin's server plugins, loaded at the next start) or added in code through the same entry:

```csharp
cli.AddPlugin<PublicApiEfCorePluginEntry>(new { connectionString = "public-api.db" });
```

Its settings (config provider `public-api-efcore` unless noted; code options are their defaults, saved settings override them):
`dialect`, `connectionString` (secret; empty: the connection of the dialect's driver plugin, database-postgres or database-mssql, which also brings the postgres and mssql providers when installed from the marketplace), `tablePrefix`. A required setting without a value lists the plugin as `needs-configuration` with `missing`; see
[Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard).

## License

MIT
