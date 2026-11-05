# Qodalis.Cli.Plugin.Audit.EfCore

Entity Framework Core store for the [Qodalis CLI Server audit log](../audit/README.md). Keeps the audit log (sign-ins, user and group changes, shells, tokens, and the host's own events) in a database, so it survives restarts and is shared by every instance of the server.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.Audit.EfCore
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.Audit.EfCore;

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        cli.AddUsers();

        // Keep the audit log in PostgreSQL (any EF Core provider works); adds the audit plugin too
        cli.AddEfCoreAuditStore(o => o.UseNpgsql(connectionString));
    });
```

The `audit_entries` table is created on first use, also inside a database that already has other tables.

## Host migrations

A host that manages its database with EF Core migrations can create the table in its own migrations instead (and fill it from its own audit table there):

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ApplyAuditStorageModel();
}
```

## Retention

Entries are kept until cleared (`DELETE /api/v1/qcli/audit`) or pruned. Unlike the in-memory store, which keeps
at most 200 failed sign-ins, the database keeps every entry, including one per failed sign-in from anyone on the
internet: **prune on a schedule**, for example with a daily job (jobs plugin):

```csharp
public sealed class PruneAuditJob : ICliJob
{
    public async Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken) =>
        context.Properties["deleted"] = await context.Services.GetRequiredService<IAuditStore>()
            .PruneAsync(DateTimeOffset.UtcNow.AddDays(-365), cancellationToken);
}

cli.AddJob(new PruneAuditJob(), o => { o.Id = "prune-audit"; o.Interval = TimeSpan.FromDays(1); o.RunOnStart = true; });
```

`GET /api/v1/qcli/audit` returns 100 entries when the request names no `limit`, and at most 1000.

## License

MIT
