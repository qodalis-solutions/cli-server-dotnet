# Qodalis.Cli.Plugin.Audit

The audit log of the Qodalis CLI Server: who did what, to whom or what, and when. It keeps the entries
(`IAuditStore`), serves them at `/api/v1/qcli/audit` and is the admin dashboard's **Audit** page and the CLI's `audit`
command. The [users plugin](../users/README.md) adds it and records sign-ins, failed sign-ins, user, group, token and
shell events; an application records its own changes in the same log.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.Audit
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.Audit.Extensions;

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        cli.AddUsers();   // adds the audit plugin too
        cli.AddAudit();   // or on its own, without the users plugin
    });
```

## Recording your own changes

Resolve `IAuditStore` (`Qodalis.Cli.Abstractions.Audit`) and log an entry:

```csharp
await auditStore.LogAsync(new AuditEntry
{
    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    Event = "role.save",
    Actor = currentUser.Email,
    Target = "role:editor",
    Properties = new() { ["permissions"] = new[] { "jobs.view" } },
});
```

An entry has the `Event`, the user it is about (`Username`), who did it (`Actor`), what else it is about (`Target`,
e.g. `group:admins`), `Details`, `Metadata` (address, session) and the application's own values (`Properties`).

## API

| Endpoint | Permission | Description |
|---|---|---|
| `GET /api/v1/qcli/audit?event=&username=&actor=&from=&to=&limit=&offset=` | `audit:read` | Entries newest first; filters are exact and case-insensitive; `from`/`to` (ISO 8601 or epoch milliseconds) keep `from <= timestamp < to`; 100 entries without a `limit`, at most 1000 |
| `DELETE /api/v1/qcli/audit` | `audit:clear` | Clears the log, then records `audit_cleared` with who did it |

The permissions apply on a protected server (users plugin, `ProtectServer`). A store of your own receives the time
range as `AuditQueryOptions.From`/`To`.

## Storage

The default store keeps the latest 1000 entries in memory (failed sign-ins apart, at most 200, so a flood of them keeps
the rest). Keep the log in a database with [Qodalis.Cli.Plugin.Audit.EfCore](../audit-efcore/README.md)
(`cli.AddEfCoreAuditStore(...)`), or implement `IAuditStore` and register it with `cli.SetAuditStore<T>()`.
`IAuditStore.PruneAsync(before)` deletes old entries; run it on a schedule for a store that keeps everything.

## License

MIT
