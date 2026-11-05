# Qodalis.Cli.Plugin.Users.EfCore

Entity Framework Core audit log store for the [Qodalis CLI Server users plugin](../users/README.md). Keeps the audit log (sign-ins, user and group changes, shells, tokens, and the host's own events) in a database, so it survives restarts and is shared by every instance of the server.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.Users.EfCore
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.Users.EfCore;

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        cli.AddUsers();

        // Keep the audit log in PostgreSQL (any EF Core provider works)
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

Entries are kept until cleared (`DELETE /api/v1/qcli/audit`) or pruned. Prune on a schedule, for example from a job:

```csharp
await services.GetRequiredService<IAuditStore>().PruneAsync(DateTimeOffset.UtcNow.AddDays(-365));
```

## License

MIT
