# Qodalis.Cli.Plugin.Users

User management plugin for [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet). Adds ASP.NET Core endpoints for authentication, users, sessions, groups and an audit log, backed by swappable storage interfaces with in-memory defaults. It is the server side of the `@qodalis/cli-users` plugin's *server mode*.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.Users
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.Users.Extensions;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddUsers(options =>
    {
        options.DefaultAdminPassword = "admin";
    });
});

var app = builder.Build();

// Registers the bearer-token auth middleware for the users routes
app.UseQodalisUsers();
```

On first start a hosted bootstrap service seeds a `root` admin user (email `root@localhost`, password from `DefaultAdminPassword`, default `root`) and an `admin` group, unless an external auth adapter is configured. Session-kick events are broadcast through the server's events WebSocket automatically.

## Options

`cli.AddUsers(Action<UsersModuleOptions>? configure = null)`

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `UserStore` | `IUserStore` | `InMemoryUserStore` | User storage |
| `SessionStore` | `ISessionStore` | `InMemorySessionStore` | Session/token storage |
| `CredentialStore` | `ICredentialStore` | `InMemoryCredentialStore` | Password hash storage |
| `AuditStore` | `IAuditStore` | `InMemoryAuditStore` | Audit log storage |
| `GroupStore` | `IGroupStore` | `InMemoryGroupStore` | Group storage |
| `SessionTimeoutMs` | `long?` | `1800000` (30 min) | Idle timeout for sessions |
| `DefaultAdminPassword` | `string?` | `"root"` | Password for the seeded `root` user |
| `AuthAdapter` | `IExternalAuthAdapter?` | `null` | Delegate authentication to the host app (see below) |
| `UseExternalAuth(Func<HttpContext, ExternalAuthResult?>)` | method | — | Delegate-based shortcut for `AuthAdapter` |

## API Endpoints

All routes live under `/api/v1/qcli`. Protected routes expect `Authorization: Bearer <token>`.

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/auth/login` | POST | None | `{ username, password }` → `{ token, session }` |
| `/auth/logout` | POST | Required | Invalidate the current token (204) |
| `/auth/verify` | POST | None | `{ userId, password }` → `{ valid }` |
| `/users` | GET | Required | List users (`?query=&skip=&take=`) |
| `/users/{idOrName}` | GET | Required | Get a user |
| `/users` | POST | Admin | Create a user `{ name, email, password?, groups?, data? }` |
| `/users/{id}` | PUT | Admin | Update a user |
| `/users/{id}` | DELETE | Admin | Delete a user and its sessions/credentials (204) |
| `/users/{id}/password` | PUT | Self or admin | `{ currentPassword?, newPassword }` (204) |
| `/sessions` | GET | Admin | List active sessions |
| `/sessions/current` | GET | Required | Current session |
| `/sessions/{id}` | DELETE | Admin | Kick a session (204) and broadcast `session-kicked` |
| `/groups` | GET | Required | List groups |
| `/groups/{name}` | GET | Required | Get a group |
| `/groups` | POST | Admin | Create a group `{ name, description? }` |
| `/groups/{name}` | DELETE | Admin | Delete a group (204) |
| `/groups/{name}/members/{userId}` | PUT | Admin | Add a user to a group (204) |
| `/groups/{name}/members/{userId}` | DELETE | Admin | Remove a user from a group (204) |
| `/audit` | GET | Admin | Audit entries (`?event=&username=&limit=`) |
| `/audit` | DELETE | Admin | Clear the audit log (204) |

Tokens are opaque random strings validated against the session store on every request; `LastActivity` is refreshed on each authenticated call.

## Session kick event

`DELETE /sessions/{id}` broadcasts the following message on the events WebSocket (`/ws/v1/qcli/events`):

```json
{ "type": "session-kicked", "data": { "sessionId": "...", "userId": "..." } }
```

A `@qodalis/cli-users` client in server mode logs itself out when its own session id is kicked.

## Custom storage

Implement any of `IUserStore`, `ISessionStore`, `ICredentialStore`, `IAuditStore` or `IGroupStore` (namespace `Qodalis.Cli.Plugin.Users.Stores`) and assign the instance in the options:

```csharp
cli.AddUsers(options =>
{
    options.UserStore = new PostgresUserStore(connectionString);
    options.SessionStore = new RedisSessionStore(redis);
});
```

## External auth adapter

If the host application already authenticates requests, provide an `IExternalAuthAdapter` or use the delegate shortcut. The adapter receives the `HttpContext` and returns an `ExternalAuthResult` (user + admin flag) or `null`. The built-in `login`/`logout`/`verify` endpoints then answer `501`, and no `root` user is seeded.

```csharp
cli.AddUsers(options =>
{
    options.UseExternalAuth(ctx =>
    {
        var principal = ctx.User;
        if (principal.Identity?.IsAuthenticated != true) return null;
        return new ExternalAuthResult { User = ToCliUser(principal), IsAdmin = principal.IsInRole("admin") };
    });
});
```

## Client

Point the browser plugin at this server:

```typescript
import { usersModule } from '@qodalis/cli-users';

engine.registerModule(usersModule.configure({ server: 'dotnet' }));
// options.servers must contain { name: 'dotnet', url: 'http://localhost:8046' }
```

The client then logs in through `/auth/login`, attaches the bearer token to every request to that server, and gains the `sessions` and `session-kick` commands.

## License

MIT
