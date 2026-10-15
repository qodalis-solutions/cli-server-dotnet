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

On start the plugin seeds the built-in groups `admin` (permissions `["*"]`) and `users` (`["commands:execute"]`) when missing, and — unless an external auth adapter is configured — a `root` admin user (email `root@localhost`) when the user store is empty. Without `DefaultAdminPassword` a random 16-character password is generated, logged **once** with a warning, and root must change it on first login (`mustChangePassword`); an explicit well-known password (`root`, `admin`, `password`) logs a warning.

`UseQodalisUsers()` must run before `UseCli()`. With `ProtectServer` (default `true`) it protects the **whole CLI server**, not only the users routes; without the middleware every protected route answers `401` (fail closed).

## Options

`cli.AddUsers(Action<UsersModuleOptions>? configure = null)`

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `SetUserStore<T>()` / `SetUserStore(sp => ...)` | method | `InMemoryUserStore` | User storage (DI-constructed singleton) |
| `SetSessionStore<T>()` / factory | method | `InMemorySessionStore` | Session/token storage |
| `SetCredentialStore<T>()` / factory | method | `InMemoryCredentialStore` | Password hash storage |
| `SetAuditStore<T>()` / factory | method | `InMemoryAuditStore` (last 1000 entries) | Audit log storage |
| `SetGroupStore<T>()` / factory | method | `InMemoryGroupStore` | Group storage |
| `SetApiTokenStore<T>()` / factory | method | `InMemoryApiTokenStore` | API token storage (hashes only) |
| `AllowNonExpiringTokens` | `bool` | `false` | Allow API tokens with `expiresInDays: null` |
| `UserStore`, `SessionStore`, ... | instance | `null` | Store instances (alternative to the `Set*Store` methods) |
| `ProtectServer` | `bool` | `true` | Require a session (and the permissions below) on every CLI route and socket |
| `SessionTimeoutMs` | `long?` | `1800000` (30 min) | Idle timeout, checked on every request |
| `MaxSessionLifetimeMs` | `long` | `43200000` (12 h) | Absolute session lifetime; `expiresAt` = min(idle, absolute) |
| `DefaultAdminPassword` | `string?` | random (logged once, must change) | Password for the seeded `root` user |
| `MinPasswordLength` / `MaxPasswordLength` | `int` | `8` / `1024` | Password policy |
| `MaxLoginAttempts` / `LockoutMs` | `int` / `long` | `5` / `300000` | Lockout after consecutive failures within `LockoutMs` (429), also for unknown names; older failures expire |
| `MaxAttemptsPerIpPerMinute` | `int` | `20` | Per-IP limit on login, verify-password and self password change (429) |
| `TrustProxy` | `bool` | `false` | Use `X-Forwarded-For` for the client IP (only behind a trusted proxy): the entry `TrustedProxyHops` from the right (the one your proxy appended), the leftmost if there are fewer entries, the remote address if absent. When `false` the header is ignored |
| `TrustedProxyHops` | `int` | `1` | Number of trusted proxies in front of the server (used when `TrustProxy` is on); client IP = entry at index `count - TrustedProxyHops` |
| `PasswordHashConcurrency` | `int` | `4` | Concurrent PBKDF2 computations |
| `AuthAdapter` / `UseExternalAuth(...)` / `UseExternalAuth<T>()` | adapter | `null` | Delegate authentication to the host app (see below) |

## Permissions

Permissions are `resource:action` strings with `*` wildcards (`*`, `users:*`), matched case-insensitively. A user's effective permissions are the union of their groups' `permissions`. Built-in permissions: `users:read|create|update|delete`, `groups:read|create|update|delete`, `sessions:read|revoke`, `audit:read|clear`, `tokens:read|revoke`, plus the server permissions below.

### Server protection (`ProtectServer`)

| Surface | Permission |
|---------|------------|
| `GET /commands` | authenticated |
| `POST /execute`, `/execute/stream` | `commands:execute` + the processor's `RequiredPermissions` |
| MCP endpoint (`POST /api/qcli/mcp`, opt-in `cli.AddMcp()`) | `commands:execute`; each tool needs its own permissions (see the main README) |
| fs read (`/api/qcli/fs/ls`, `cat`, `stat`, `download`) | `files:read` |
| fs write (`upload`, `mkdir`, `rm`, …) | `files:write` |
| data explorer (`/api/qcli/data-explorer/*`) | `data:query` |
| jobs read / manage (`/api/v1/qcli/jobs`) | `jobs:read` / `jobs:manage` |
| admin dashboard API | `admin:access` |
| events WebSocket | authenticated |
| logs WebSocket | `logs:read` |
| shell WebSocket (opt-in `cli.EnableShell()`; required even with `ProtectServer = false`) | `shell:execute` |

Public: `POST /sessions`, `POST /auth/login` (admin dashboard), `/api/qcli/versions`, `/api/cli/versions`, `/api/v1/qcli/version`, `/api/v1/qcli/capabilities`.

**Shell sessions** are bound to the session that opened them: logout, expiry, revoke, user disabled/deleted or losing `shell:execute` closes them with `4401`. Audit events `shell_opened` / `shell_closed` (`metadata: { mode, sessionId, durationMs, exitCode, reason }`, never the command). Shell rejections are close codes (`4401`/`4403`/`4404`/`4429`/`4408`), not HTTP statuses. See the main README, "Shell security".

**Events and logs sockets** are bound to the stored session that opened them: when it ends (logout, revoke, user disabled/deleted, expiry) the socket closes with `4401 session ended`; for a revoke, `session-kicked` is sent first. Sockets authenticated by an external adapter have no stored session and are not re-checked.

**WebSockets** authenticate with a single-use ticket: `POST /sessions/ws-ticket` → `201 { ticket, expiresIn: 30 }`, then connect with `?ticket=<ticket>`. Bearer tokens are never accepted in query strings. Upgrades are checked against `cli.SetAllowedOrigins(...)`.

**Anti-escalation:** callers can only grant permissions they hold (group permissions, group membership, user `groups`), and can only act on users whose permissions they cover (403 `insufficient privileges for this user`).

**CSRF:** state-changing requests without a bearer token are refused (403) when they carry a disallowed `Origin`; in external-adapter (cookie) mode they must carry an allowed one.

**Host routes:** protect your own endpoints with `[QcliAuthorize("perm")]` (MVC) or `.RequireQcliAuthorization("perm")` (minimal APIs).

The admin dashboard plugin authenticates through the users plugin when both are installed (login requires `admin:access`).

## API Endpoints

All routes live under `/api/v1/qcli` and accept a trailing slash. Protected routes expect `Authorization: Bearer <token>`. Every error body is `{ "error": "..." }`.

| Endpoint | Method | Requirement | Description |
|----------|--------|-------------|-------------|
| `/sessions` | POST | public | Login by name or email: `{ username, password }` → `201 { token, session }` |
| `/sessions/ws-ticket` | POST | authenticated | Single-use WebSocket ticket → `201 { ticket, expiresIn }` |
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
| `/users/{idOrName}/password` | PUT | `users:update`, or self with `currentPassword` | `{ password, currentPassword?, temporary? }` → 204; a reset of another user sets `mustChangePassword` unless `temporary: false` |
| `/users/{idOrName}/verify-password` | POST | self or `users:update` | `{ password }` → `{ valid }`; failures count toward lockout |
| `/groups` | GET | authenticated | List groups |
| `/groups/{name}` | GET | authenticated | Get a group |
| `/groups/{name}/members` | GET | `groups:read` or member | Members |
| `/groups` | POST | `groups:create` | `{ name, description?, permissions? }` → 201 |
| `/groups/{name}` | PUT | `groups:update` | `{ description?, permissions? }` (`admin` permissions are fixed) |
| `/groups/{name}` | DELETE | `groups:delete` | Delete (204); removed from every user; built-ins are protected |
| `/groups/{name}/members/{idOrName}` | POST / DELETE | `groups:update` | Add / remove a member (204) |
| `/tokens` | GET | authenticated | Own API tokens; `?all=true` → every user's (`tokens:read`) |
| `/tokens` | POST | authenticated, **session only** | `{ name, expiresInDays?, permissions? }` → `201 { token, apiToken }` |
| `/tokens/{id}` | DELETE | owner or `tokens:revoke` | Revoke (204); unknown or not yours → 404 |
| `/audit` | GET | `audit:read` | Audit entries (`?event=&username=&limit=`) |
| `/audit` | DELETE | `audit:clear` | Clear the audit log (204) |

While `mustChangePassword` is true a session may only call `GET/DELETE /sessions/current` and `PUT /users/{self}/password` (others → 403 `password change required`). Session stores keep only `SHA-256(token)`. Names: 1–64 chars `^[A-Za-z0-9][A-Za-z0-9._@-]*$`; names and emails share one uniqueness namespace; control characters are rejected.

Protections (409): deleting yourself; deleting, disabling or removing `admin` from the last enabled admin; deleting a built-in group; duplicate user name/email or group name. Disabling a user or changing a password revokes sessions. Passwords are stored as PBKDF2-HMAC-SHA256 (`pbkdf2$100000$<salt>$<hash>`).

The admin dashboard plugin keeps its own login at `/auth/login`; both plugins can be mounted at the same prefix.

## API tokens

Long-lived, revocable bearer tokens for MCP clients, CI and scripts (sessions expire and need a password sign-in).
A token looks like `qcli_pat_<43 base64url chars>` and is accepted everywhere a session token is:
`Authorization: Bearer qcli_pat_…`. Only `SHA-256(token)` is stored; the token is shown once, at creation.

```http
POST /api/v1/qcli/tokens
Authorization: Bearer <session token>

{ "name": "claude", "expiresInDays": 30, "permissions": ["commands:execute", "files:read"] }
```

```json
201
{
  "token": "qcli_pat_…",
  "apiToken": { "id": "…", "name": "claude", "userId": "…", "userName": "root",
                "createdAt": 1760000000000, "expiresAt": 1762592000000, "lastUsedAt": null,
                "permissions": ["commands:execute", "files:read"] }
}
```

- `name`: 1-64 characters of `[A-Za-z0-9._ -]`, unique per user (case-insensitive; duplicate → 409).
- `expiresInDays`: integer 1-365, default 90. `null` (never expires) only with `AllowNonExpiringTokens = true`, else 400.
- `permissions` (optional scope): every entry must be a valid permission held by the caller
  (else 403 `forbidden: cannot grant <perm>`). The token's effective permissions are the user's **live**
  permissions, narrowed to the scope entries the user still holds.
- At most 20 (non-expired) tokens per user (400).
- Creating a token **requires a session**: with an API token → 403 `forbidden: API tokens cannot create tokens`.
  With an external auth adapter token creation answers 501.
- Views (`GET /tokens`) never contain the token or its hash: `{ id, name, userId, userName, createdAt, expiresAt,
  lastUsedAt, permissions }` (`permissions` omitted when unscoped; `lastUsedAt` is updated at most once a minute).

Authentication with a token: unknown, revoked or expired token → 401; user missing or disabled → 401;
`mustChangePassword` → 403 like sessions. `GET`/`DELETE /sessions/current` with a token → 404 `not a session`;
password changes with a token → 403 `forbidden: API tokens cannot change passwords`. Sockets opened with a token
(WebSocket tickets can be requested with a token) close with `4401 session ended` when the token is revoked or
expires, or the user is disabled. Deleting a user deletes their tokens; disabling a user makes them fail.
Audit events: `token_created`, `token_revoked` (details: token name and id; `metadata: { tokenId, tokenName }`).

Tokens are the way to connect MCP clients to the server's [MCP endpoint](../../README.md#mcp-endpoint):

```bash
claude mcp add --transport http qcli http://localhost:8046/api/qcli/mcp \
  --header "Authorization: Bearer <token>"
```

## Session kick event

Every revoked session (explicit revoke, user deleted/disabled, password changed) is sent on the events WebSocket — only to sockets authenticated as that user:

```json
{ "type": "session-kicked", "data": { "sessionId": "...", "userId": "...", "reason": "revoked" } }
```

`reason` is one of `revoked`, `user_deleted`, `user_disabled`, `password_changed`. A `@qodalis/cli-users` client in server mode logs itself out when its own session id is kicked.

## Custom storage

Implement any of `IUserStore`, `ISessionStore`, `ICredentialStore`, `IAuditStore`, `IGroupStore` or `IApiTokenStore` (namespace `Qodalis.Cli.Plugin.Users.Stores`) and register it:

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

**Shells opened by adapter users** are bound like stored sessions (spec C2): at registration and at every
re-check (`SessionCheckInterval`, and on user/group changes) the adapter is run again on a snapshot of the
request it accepted — the shell upgrade, or the `POST /sessions/ws-ticket` request when a ticket was used. The
snapshot holds only request data (method, URL, headers including cookies, remote address); the original
`HttpContext` with its features and items is gone. Adapters used with shells must therefore authenticate from
headers or cookies — directly, or through the host's default ASP.NET Core authentication scheme, which is run on
the snapshot so `ctx.User` is set as in the example above. `null`, a different user or an exception closes the
shell with `4401 session ended`; a user without `shell:execute` closes it with `4401 session ended` too (audit
reason `permission removed`).

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
