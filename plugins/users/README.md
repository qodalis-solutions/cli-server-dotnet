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

On start the plugin seeds the built-in groups `admin` (permissions `["*"]`) and `users` (no permissions) when missing, and — unless an external auth adapter is configured — a `root` admin user (email `root@localhost`, password from `DefaultAdminPassword`, default `root`) when the user store is empty. Without `UseQodalisUsers()` every endpoint except login answers `401` (fail closed).

## Options

`cli.AddUsers(Action<UsersModuleOptions>? configure = null)`

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `SetUserStore<T>()` / `SetUserStore(sp => ...)` | method | `InMemoryUserStore` | User storage (DI-constructed singleton) |
| `SetSessionStore<T>()` / factory | method | `InMemorySessionStore` | Session/token storage |
| `SetCredentialStore<T>()` / factory | method | `InMemoryCredentialStore` | Password hash storage |
| `SetAuditStore<T>()` / factory | method | `InMemoryAuditStore` (last 1000 entries) | Audit log storage |
| `SetGroupStore<T>()` / factory | method | `InMemoryGroupStore` | Group storage |
| `UserStore`, `SessionStore`, ... | instance | `null` | Store instances (alternative to the `Set*Store` methods) |
| `SessionTimeoutMs` | `long?` | `1800000` (30 min) | Idle timeout, checked on every request |
| `DefaultAdminPassword` | `string?` | `"root"` | Password for the seeded `root` user |
| `MinPasswordLength` | `int` | `4` | Password policy |
| `MaxLoginAttempts` / `LockoutMs` | `int` / `long` | `5` / `300000` | Lockout after consecutive failures (429) |
| `AuthAdapter` / `UseExternalAuth(...)` / `UseExternalAuth<T>()` | adapter | `null` | Delegate authentication to the host app (see below) |

## Permissions

Permissions are `resource:action` strings with `*` wildcards (`*`, `users:*`), matched case-insensitively. A user's effective permissions are the union of their groups' `permissions`. Built-in permissions: `users:read|create|update|delete`, `groups:read|create|update|delete`, `sessions:read|revoke`, `audit:read|clear`.

## API Endpoints

All routes live under `/api/v1/qcli` and accept a trailing slash. Protected routes expect `Authorization: Bearer <token>`. Every error body is `{ "error": "..." }`.

| Endpoint | Method | Requirement | Description |
|----------|--------|-------------|-------------|
| `/sessions` | POST | public | Login by name or email: `{ username, password }` → `201 { token, session }` |
| `/sessions/current` | GET | authenticated | `{ id, user, permissions, loginTime, lastActivity, expiresAt }` |
| `/sessions/current` | DELETE | authenticated | Logout (204) |
| `/sessions` | GET | `sessions:read` | Active sessions (never includes tokens) |
| `/sessions/{id}` | DELETE | `sessions:revoke` or own session | Revoke (204) and broadcast `session-kicked` |
| `/users` | GET | `users:read` | List users (`?query=&skip=&take=`) |
| `/users/{idOrName}` | GET | `users:read` or self | Get a user |
| `/users/{idOrName}/permissions` | GET | `users:read` or self | `{ userId, groups, permissions }` |
| `/users` | POST | `users:create` | `{ name, email, groups?, password?, disabled?, homeDir?, data? }` → 201 |
| `/users/{idOrName}` | PUT | `users:update` (self: `email`, `data` only) | Update a user |
| `/users/{idOrName}` | DELETE | `users:delete` | Delete (204); credentials and sessions are removed |
| `/users/{idOrName}/password` | PUT | self (with `currentPassword`) or `users:update` | `{ password, currentPassword? }` → 204 |
| `/users/{idOrName}/verify-password` | POST | self or `users:update` | `{ password }` → `{ valid }`; failures count toward lockout |
| `/groups` | GET | authenticated | List groups |
| `/groups/{name}` | GET | authenticated | Get a group |
| `/groups/{name}/members` | GET | `groups:read` or member | Members |
| `/groups` | POST | `groups:create` | `{ name, description?, permissions? }` → 201 |
| `/groups/{name}` | PUT | `groups:update` | `{ description?, permissions? }` (`admin` permissions are fixed) |
| `/groups/{name}` | DELETE | `groups:delete` | Delete (204); removed from every user; built-ins are protected |
| `/groups/{name}/members/{idOrName}` | POST / DELETE | `groups:update` | Add / remove a member (204) |
| `/audit` | GET | `audit:read` | Audit entries (`?event=&username=&limit=`) |
| `/audit` | DELETE | `audit:clear` | Clear the audit log (204) |

Protections (409): deleting yourself; deleting, disabling or removing `admin` from the last enabled admin; deleting a built-in group; duplicate user name/email or group name. Disabling a user or changing a password revokes sessions. Passwords are stored as PBKDF2-HMAC-SHA256 (`pbkdf2$100000$<salt>$<hash>`).

The admin dashboard plugin keeps its own login at `/auth/login`; both plugins can be mounted at the same prefix.

## Session kick event

Every revoked session (explicit revoke, user deleted/disabled, password changed) broadcasts on the events WebSocket:

```json
{ "type": "session-kicked", "data": { "sessionId": "...", "userId": "...", "reason": "revoked" } }
```

`reason` is one of `revoked`, `user_deleted`, `user_disabled`, `password_changed`. A `@qodalis/cli-users` client in server mode logs itself out when its own session id is kicked.

## Custom storage

Implement any of `IUserStore`, `ISessionStore`, `ICredentialStore`, `IAuditStore` or `IGroupStore` (namespace `Qodalis.Cli.Plugin.Users.Stores`) and register it:

```csharp
cli.AddUsers(options => options
    .SetUserStore<PostgresUserStore>()          // constructed via DI
    .SetSessionStore(sp => new RedisSessionStore(sp.GetRequiredService<IConnectionMultiplexer>())));
```

## External auth adapter

If the host application already authenticates requests, provide an `IExternalAuthAdapter` or use the delegate shortcut. The adapter receives the `HttpContext` and returns an `ExternalAuthResult` (user + admin flag) or `null`. Login (`POST /sessions`), logout (`DELETE /sessions/current`), `verify-password` and password changes then answer `501`, no `root` user is seeded (the built-in groups still are), and permissions come from the groups the adapter returns (`IsAdmin` adds `admin`).

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

The client then logs in through `POST /sessions`, attaches the bearer token to every request to that server, and gains the `sessions` and `session-kick` commands.

## License

MIT
