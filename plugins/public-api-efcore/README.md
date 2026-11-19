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

The `qcli_api_usage` table is created on first use, also inside a database that already has other tables.

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
| `UserId` | string, 128 | index (with `Window`); the same limit as `api_tokens.UserId`: user ids (e.g. from an external adapter) must fit |
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

## License

MIT
