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

On start the plugin seeds the built-in groups `admin` (permissions `["*"]`) and `users` (`["commands:execute"]`) when missing, and — unless an external auth adapter is configured — a `root` admin user (email `root@localhost`) when the user store is empty. Without `DefaultAdminPassword` a random 16-character password is generated and printed **once** to standard error (never through `ILogger`, so it does not reach the `/ws/v1/qcli/logs` stream or the admin log buffer; a warning without the password is logged), and root must change it on first login (`mustChangePassword`); an explicit well-known password (`root`, `admin`, `password`) logs a warning.

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
| `LinkApprovalUrl` | `string?` | `/qcli/admin/link` | The page where users approve a terminal's link request (absolute or root-relative; `{code}` is replaced, otherwise `?code=` is appended) |
| `UserStore`, `SessionStore`, ... | instance | `null` | Store instances (alternative to the `Set*Store` methods) |
| `ProtectServer` | `bool` | `true` | Require a session (and the permissions below) on every CLI route and socket |
| `SessionTimeoutMs` | `long?` | `1800000` (30 min) | Idle timeout, checked on every request |
| `MaxSessionLifetimeMs` | `long` | `43200000` (12 h) | Absolute session lifetime; `expiresAt` = min(idle, absolute) |
| `DefaultAdminPassword` | `string?` | random (printed once to stderr, must change) | Password for the seeded `root` user |
| `MinPasswordLength` / `MaxPasswordLength` | `int` | `8` / `1024` | Password policy |
| `MaxLoginAttempts` / `LockoutMs` | `int` / `long` | `5` / `300000` | Lockout after consecutive failures within `LockoutMs` (429), also for unknown names; older failures expire |
| `MaxAttemptsPerIpPerMinute` | `int` | `20` | Per-IP limit on login, verify-password and self password change (429) |
| `TrustProxy` | `bool` | `false` | Use `X-Forwarded-For` for the client IP (only behind a trusted proxy): the entry `TrustedProxyHops` from the right (the one your proxy appended), the leftmost if there are fewer entries, the remote address if absent. When `false` the header is ignored |
| `TrustedProxyHops` | `int` | `1` | Number of trusted proxies in front of the server (used when `TrustProxy` is on); client IP = entry at index `count - TrustedProxyHops` |
| `PasswordHashConcurrency` | `int` | `4` | Concurrent PBKDF2 computations |
| `UsersReadOnly` / `ReadOnlyUsers()` | `bool` | `false` | The host owns the users: user writes answer `501` (see [Read-only stores](#read-only-stores)) |
| `GroupsReadOnly` / `ReadOnlyGroups()` | `bool` | `false` | The host owns the groups: group writes answer `501` |
| `AuthAdapter` / `UseExternalAuth(...)` (sync or async delegate, or an instance) / `UseExternalAuth<T>()` | adapter | `null` | Delegate authentication to the host app (see below) |
| `SetHostUser` | `bool` | `true` | Set `HttpContext.User` to the caller on CLI requests and guarded host routes (see [The host's user](#the-hosts-user-sethostuser)) |
| `GuardOnly` / `UseGuardOnly()` | `bool` | `false` | Only the guards: no users routes, no seeding, no server protection (see [Guard-only mode](#guard-only-mode-a-second-service)) |

## Permissions

Permissions are `resource:action` strings with `*` wildcards (`*`, `users:*`), matched case-insensitively. A user's effective permissions are the union of their groups' `permissions`. Built-in permissions: `users:read|create|update|delete`, `groups:read|create|update|delete`, `sessions:read|revoke`, `audit:read|clear`, `tokens:read|revoke`, plus the server permissions below.

The plugin adds its 12 permissions (`users:*`, `groups:*`, `sessions:*`, `tokens:*`, groups Users, Groups, Sessions and Tokens) to the server's [permission catalog](../../README.md#permission-catalog), next to core's 11, so the dashboard and the console can offer them by name; `GET /permissions` serves the catalog. The catalog is for choosing only: group permissions and `cli` tokens still accept any permission.

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

**Events and logs sockets** are bound to the stored session that opened them: when it ends (logout, revoke, user disabled/deleted, expiry) the socket closes with `4401 session ended`; for a revoke, `session-kicked` is sent first. Sockets opened through an external adapter are re-checked through the adapter every `SessionCheckInterval` (see [External auth adapter](#external-auth-adapter)).

**WebSockets** authenticate with a single-use ticket: `POST /sessions/ws-ticket` → `201 { ticket, expiresIn: 30 }`, then connect with `?ticket=<ticket>`. Bearer tokens are never accepted in query strings. Upgrades are checked against `cli.SetAllowedOrigins(...)`.

**Anti-escalation:** callers can only grant permissions they hold (group permissions, group membership, user `groups`), and can only act on users whose permissions they cover (403 `insufficient privileges for this user`).

**CSRF:** state-changing requests without a bearer token are refused (403) when they carry a disallowed `Origin`; in external-adapter (cookie) mode they must carry an allowed one.

**Host routes:** protect your own endpoints with `[QcliAuthorize("perm")]` (MVC) or `.RequireQcliAuthorization("perm")` (minimal APIs). These guards are the only routes that accept [host tokens](#host-tokens-iapitokenmanager).

The admin dashboard plugin authenticates through the users plugin when both are installed (login requires `admin:access`).

## API Endpoints

All routes live under `/api/v1/qcli` and accept a trailing slash. Protected routes expect `Authorization: Bearer <token>`. Every error body is `{ "error": "..." }`.

| Endpoint | Method | Requirement | Description |
|----------|--------|-------------|-------------|
| `/sessions` | POST | public | Login by name or email: `{ username, password }` → `201 { token, session }` |
| `/sessions/ws-ticket` | POST | authenticated | Single-use WebSocket ticket → `201 { ticket, expiresIn }` |
| `/sessions/current` | GET | authenticated | `{ id, user, permissions, loginTime, lastActivity, expiresAt, usersReadOnly, groupsReadOnly }` |
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
| `/tokens` | POST | authenticated, **session only** | `{ name, expiresInDays?, permissions?, audience? }` → `201 { token, apiToken }` (`audience` only `"cli"`) |
| `/tokens/{id}` | DELETE | owner or `tokens:revoke` | Revoke (204); unknown or not yours → 404 |
| `/permissions` | GET | authenticated | The permission catalog entries the caller holds; `?all=true` → every entry (`users:read`) |
| `/link` | POST | public, 10 per minute per address | `{ client, permissions? }` → `201 { linkId, userCode, verificationUrl, expiresIn, interval }` (see [Linking a terminal](#linking-a-terminal)) |
| `/link/{linkId}` | GET | public | `{ status: "pending" \| "denied" }`, or `{ status: "approved", token, userName, expiresAt }` once; 404 `{ status: "expired" }` |
| `/link/code/{userCode}` | GET | authenticated | The request the approval page shows |
| `/link/code/{userCode}/approve` \| `deny` | POST | authenticated, **session only** (approve) | Approve creates the approver's API token for the terminal → `{ tokenId, tokenName }`; deny → 204 |
| `/audit` | GET | `audit:read` | Audit entries, newest first (`?event=&username=&actor=&limit=&offset=`) |
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
                "audience": "cli", "permissions": ["commands:execute", "files:read"] }
}
```

- `name`: 1-64 characters of `[A-Za-z0-9._ -]`, unique per user (case-insensitive; duplicate → 409).
- `expiresInDays`: integer 1-365, default 90. `null` (never expires) only with `AllowNonExpiringTokens = true`, else 400.
- `permissions` (optional scope): every entry must be a valid permission held by the caller
  (else 403 `forbidden: cannot grant <perm>`). The token's effective permissions are the user's **live**
  permissions, narrowed to the scope entries the user still holds.
- `audience` (optional): absent or `"cli"`. `"host"` → 400 `host tokens can only be created by the host application`
  (see [Host tokens](#host-tokens-iapitokenmanager)); any other value → 400 `audience must be cli or host`.
- At most 20 (non-expired) tokens per user (400), both audiences together; names are unique across both audiences.
- Creating a token **requires a session**: with an API token → 403 `forbidden: API tokens cannot create tokens`.
  With an external auth adapter token creation answers 501.
- Views (`GET /tokens`) never contain the token or its hash: `{ id, name, userId, userName, createdAt, expiresAt,
  lastUsedAt, audience, permissions }` (`audience` is always present, `"cli"` or `"host"`: a stored value other than exactly `cli` is shown as `host`; `permissions` omitted when
  unscoped; `lastUsedAt` is updated at most once a minute). A stored token without an audience (created before
  1.0.0-beta.29, or kept by a store that drops the field) reads as `"cli"`.

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

### The permission catalog (`GET /permissions`)

`GET /api/v1/qcli/permissions` returns the [catalog](../../README.md#permission-catalog) entries, in catalog order
(`cli` entries, then the host's), for pickers that group them by `group`:

```json
[{ "name": "commands:execute", "description": "Run commands on the server", "group": "Commands", "surface": "cli" }, …]
```

| Request | Result |
|---|---|
| no query, or `?all=false` | 200, the entries the caller holds (wildcards match: `rates:*` covers `rates:read`); a scoped token sees its scope |
| `?all=true` with `users:read` | 200, every entry |
| `?all=true` without `users:read` | 403 `permission denied: users:read` |
| `?all=<anything else>` | 400 `all must be true or false` |
| no credentials | 401 `unauthorized` |
| a `host` token | 403 `forbidden: host tokens cannot use CLI routes` |

### Host tokens (`IApiTokenManager`)

Your app can hand its users API tokens for **its own** routes: `host` tokens. They carry only permissions your app
declared with `cli.AddPermissions(...)` (the catalog's `host` entries) and are accepted only by the
`[QcliAuthorize]` / `.RequireQcliAuthorization(...)` guards, which check them against the user's live permissions as
for any token.

```csharp
app.MapGet("/api/rates", GetRates).RequireQcliAuthorization("rates:read");

app.MapPost("/account/tokens", async (IApiTokenManager tokens, ClaimsPrincipal user) =>
{
    var created = await tokens.CreateAsync(user.FindFirstValue("sub")!, new ApiTokenCreateOptions
    {
        Name = "rates site",
        Audience = ApiTokenAudience.Host,
        Permissions = ["rates:read"],
    });
    return Results.Ok(created); // { token, apiToken }: the token is shown once
});
```

`IApiTokenManager` (a singleton registered by `AddUsers`):

| Method | Description |
|---|---|
| `CreateAsync(userId, ApiTokenCreateOptions, ct)` | Creates a `cli` or `host` token for the user → `CreatedApiToken { Token, ApiToken }` (same JSON as `POST /tokens`) |
| `ListAsync(userId, ct)` | The user's tokens, both audiences, oldest first (`[]` for an unknown user) |
| `RevokeAsync(userId, tokenId, actor?, ct)` | `true` after revoking (also an expired token, audited like any other); `false` when the token does not exist or belongs to another user |
| `GetGrantablePermissionsAsync(userId, audience = "cli", ct)` | Catalog entries the user holds: for `host` only the `host` entries |

`ApiTokenCreateOptions`: `Name` (required), `ExpiresInDays` (1-365, default 90), `NeverExpires` (needs
`AllowNonExpiringTokens`), `Permissions` (`null` = unscoped, `cli` only), `Audience` (`"cli"` default or `"host"`),
`Actor` (the audit actor's name; default the owner's). Same validation, limits, audit entries (`token_created`,
`token_revoked`) and events (`token:created`, `token:revoked`) as the HTTP routes. Errors are `UsersApiException`s
with the status and text the route would answer; checked in this order:

1. API tokens off with an external adapter (no `AllowApiTokens()`) → 501 `Authentication is managed by the host application`.
2. Unknown user (by id) → 404 `user not found`; disabled → 403 `account disabled`.
3. Audience not `cli`/`host` → 400 `audience must be cli or host`.
4. Name and expiry as `POST /tokens`; `ExpiresInDays` with `NeverExpires` → 400 `expiresInDays and neverExpires cannot both be set`.
5. Permission syntax → 400 `invalid permission: <p>`.
6. `host` only: no permissions → 400 `host tokens need permissions`; an entry that is not a `host` catalog entry
   (case-insensitive; wildcards never match) → 400 `not a host permission: <p>`. Entries are stored with the
   catalog's spelling (`RATES:READ` → `rates:read`).
7. Each permission held by the user's live permissions (wildcards count) → else 403 `forbidden: cannot grant <p>`.
8. Duplicate name → 409 `token '<name>' already exists`; 20 tokens → 400 `a user may have at most 20 tokens`.

A null or empty `userId` throws `ArgumentException`. The `CancellationToken` parameters are not observed yet.
The catalog is consulted only when a token is created: a host token keeps a permission your app stops registering,
and the guards check the user's live permissions on every request.

**Where host tokens are refused.** Every request carrying a `host` token (any stored audience other than exactly
`cli`) on a route the users plugin answers or a non-public CLI route answers 403
`{"error":"forbidden: host tokens cannot use CLI routes"}`, before any permission check and whether or not
`ProtectServer` is on: the users routes (`/users`, `/sessions`, `/groups`, `/audit`, `/tokens`, `/link/code/…`,
`/permissions`), commands, MCP, files, data explorer, jobs, the admin API, and the events and logs socket upgrades
(HTTP 403). The shell socket closes with `4403` and the same text. An **expired** host token is answered like any expired
token (401 `invalid or expired token`) wherever the request is authenticated (users routes, the shell, every CLI
route with `ProtectServer`); on an unprotected server's CLI routes it is still refused with the 403. A host token cannot get a WebSocket ticket. On an
unprotected server only `qcli_pat_` bearers are looked up (in the token store only: no `lastUsedAt` update, no
adapter call; a failing store is logged and ignored); anonymous and `cli`-token requests behave as before. Public
routes (sign-in, link requests, version discovery) do not check. `ICliAuthBridge.AuthenticateAsync` throws
`CliAuthException(403, …)` for host tokens and `RevalidateAsync` returns `null`.

The EF Core store keeps the audience in the `api_tokens.Audience` column; see
[Qodalis.Cli.Plugin.Users.EfCore](../users-efcore/README.md#upgrading-from-100-beta28-or-older-the-audience-column)
for upgrading an existing table.

### The caller of a guarded route (`GetQcliCaller`)

`[QcliAuthorize]`, `.RequireQcliAuthorization(...)` and the public-api plugin's `RequirePublicApi` leave the caller on
the request:

```csharp
app.MapGet("/api/rates", (HttpContext ctx) =>
{
    QcliCaller caller = ctx.GetQcliCaller()!; // namespace Qodalis.Cli.Plugin.Users.Authorization
    return rates.For(caller.UserId);
}).RequireQcliAuthorization("rates:read");
```

`QcliCaller(UserId, UserName, TokenId, Audience, Permissions)`: `TokenId` and `Audience` (`cli` or `host`; any stored
audience other than exactly `cli` reads as `host`) are null for a session or an external-adapter caller;
`Permissions` are the live effective permissions, narrowed by the token's scope. With `SetHostUser` (default) the
guard also sets `HttpContext.User` (`CliHostUser`) unless the host's own authentication already set an authenticated
user; for a token it carries `qcli:token` (the token id, `CliHostUser.TokenClaim`) and `qcli:audience`
(`CliHostUser.AudienceClaim`). `GetUsersAuth()` keeps working.

### Host-token-only routes (`HostTokenOnly`)

```csharp
app.MapGet("/v1/banks", GetBanks).RequireQcliAuthorization("banks:read", hostTokenOnly: true);

[QcliAuthorize("rates:read", HostTokenOnly = true)]
public IActionResult GetRates() => ...;
```

Order: authentication (401 `unauthorized`; 401 `invalid or expired token` for an unknown or expired token or one whose
user is missing or disabled; 401 `account disabled` for a disabled session user or auth-adapter caller; 403 `password
change required`), then a session, an adapter caller or a `cli` token gets 403 `forbidden: a host token is required`, then the
permission (403 `permission denied: <p>`). Without `HostTokenOnly` nothing changes.

Both guards leave `IQcliAuthorizeMetadata` (`Permission`, `HostTokenOnly`) on the endpoint
(`QcliAuthorizeEndpointMetadata` for minimal APIs, the attribute itself for MVC); the public-api plugin turns it into
`x-required-permission` and `x-host-token-only` in the public OpenAPI document.

### Token usage (`IApiTokenUsageProvider`)

When an `IApiTokenUsageProvider` is registered (the public-api plugin registers one), `GET /tokens` (also
`?all=true`) and `IApiTokenManager.ListAsync` add `usage` as the last field of every view:

```json
"usage": { "today": 12, "last30Days": 340, "days": [ { "date": "2026-10-21", "requests": 0 }, "… 30 entries, oldest first, the last one today (UTC)" ] }
```

The provider is called once per listing with every token id. A failing provider is logged and the tokens are listed
without `usage`; `POST /tokens` and `CreateAsync` never include it. Without a provider the views are unchanged.

### Guard-only mode (a second service)

A second service that only validates tokens (it serves the host's public API and shares the main service's stores):

```csharp
builder.Services.AddControllers().AddCli(cli =>
{
    cli.WithoutCliRoutes();                       // no CLI routes or sockets; UseCli() throws
    cli.AddPermissions(p => p.Group("Rates").Add("rates:read", "Official exchange rates"));
    cli.AddUsers(o =>
    {
        o.UseGuardOnly();
        o.SetUserStore<MyUserStore>();             // the stores the main service uses
        o.SetGroupStore<MyGroupStore>();
    });
    cli.AddEfCoreApiTokenStore(o => o.UseNpgsql(connectionString));
});
```

Guard-only mode requires `cli.WithoutCliRoutes()` (in either order): otherwise startup fails with
`InvalidOperationException("guard-only needs WithoutCliRoutes()")`, so CLI routes are never served unprotected.
In guard-only mode the plugin serves no users routes (sign-in, users, groups, sessions, tokens, permissions, link,
audit), seeds nothing (no `root`, no built-in groups), sweeps nothing and does not protect the server
(`ProtectServer` is ignored). The guards authenticate `qcli_pat_` tokens against the stores. `UseQodalisUsers()` adds
no middleware without CLI routes.
`IApiTokenManager` stays available, but tokens must be created and revoked from **one service only**: names and the
20-token limit are checked in the process that creates the token.

### The host's user (`SetHostUser`)

When the plugin authenticates a CLI request itself (a session, an API token or a WebSocket ticket), it also sets
`HttpContext.User`, so the host's own code that reads the signed-in user the usual way sees the caller. For example,
a host command run from a terminal linked with `server add` calls a service that reads the user id from
`HttpContext.User`. The identity's authentication type is `CliHostUser.AuthenticationType` (`"QodalisCli"`), with
`NameIdentifier` and `sub` set to the user id (with external auth, the id your adapter returned), `Name`, `Email`,
one `qcli:permission` claim per effective permission and `qcli:auth` = `token` or `session`. Check `qcli:auth` to
refuse API tokens for an operation that needs an interactive sign-in.

- A user the host's own authentication already set (its JWT or cookie) is kept.
- Only requests the CLI handles get it: an API token never signs in to the host's other endpoints.
- A command's `RequiredPermissions` are still checked against the token's own (possibly narrowed) permissions
  first; the host's services then apply their own checks to the user.
- Turn it off with `o.SetHostUser = false`.

## Linking a terminal

`server add` in the web CLI links a terminal to the server through the browser, like the OAuth device flow:

1. The terminal sends `POST /link` with its name (`{ "client": "Qodalis CLI on Pixel 7" }`, optionally `permissions` for a scoped token) and gets a `linkId` (secret), a code such as `ABCD-2345` and the approval page (`/qcli/admin/link?code=ABCD-2345`, the admin dashboard).
2. The user opens the page, signs in, compares the code and approves. Approving creates an API token for that user named `link <client>`, with the requested permissions (never more than the user has), listed and revocable like any token.
3. The terminal polls `GET /link/{linkId}` every `interval` seconds and receives the token once.

Requests expire after 10 minutes, are single-use and live in memory. Approvals and denials are audited (`link_approved`, `link_denied`; `metadata: { client, origin, tokenId }`). Hosts with their own page set `LinkApprovalUrl`:

```csharp
builder.Services.AddControllers().AddCli(cli => cli.AddUsers(o => o.LinkApprovalUrl = "https://app.example.com/cli/link/{code}"));
```

## Events

The plugin publishes its changes on the server's event bus (`ICliEventBus`, see the main README); the events
WebSocket sends them to the sockets allowed to see them. Every event carries `actor` (who did it) when known.

| Type | Fields | Audience |
|---|---|---|
| `user:created`, `user:updated`, `user:deleted` | userId, username | `users:read` |
| `group:created`, `group:updated`, `group:deleted` | group | `groups:read` |
| `token:created`, `token:revoked` | tokenId, name, userId | `tokens:read` or the token's owner |
| `session:opened`, `session:closed` | userId, username, sessionId | `sessions:read` |

`user:updated` covers profile, password, enabled/disabled and group membership changes (deleting a group publishes it
for each member). `session:closed` covers sign-out, expiry (on use or by the idle sweep) and revocation; deleting a user
publishes `token:revoked` for each of their tokens. The user-scoped `session-kicked` signal below stays as it is.

## Session kick event

Every revoked session (explicit revoke, user deleted/disabled, password changed) is sent on the events WebSocket — only to sockets authenticated as that user:

```json
{ "type": "session-kicked", "data": { "sessionId": "...", "userId": "...", "reason": "revoked" } }
```

`reason` is one of `revoked`, `user_deleted`, `user_disabled`, `password_changed`. A `@qodalis/cli-users` client in server mode logs itself out when its own session id is kicked.

## Audit log

Every entry has the `event`, the user it is about (`username`), who did it (`actor`), `details` and `metadata`
(address, session, shell). Two optional fields make it fit an application's own changes:

- `target`: what the event is about when it is not a user, e.g. `group:admins` (the plugin's group events), a role or a job.
- `properties`: the application's own values, e.g. the old and new value of a setting.

The log is the [audit plugin](../audit/README.md)'s, which `AddUsers` adds: an application records its own changes in
the same log through `IAuditStore.LogAsync`, so the dashboard's Audit page, the `audit` command and `GET /audit` show
them all. Keep it in a database with [Qodalis.Cli.Plugin.Audit.EfCore](../audit-efcore/README.md)
(`cli.AddEfCoreAuditStore(...)`); `SetAuditStore` here does the same as `cli.SetAuditStore<T>()`.

## Custom storage

Implement any of `IUserStore`, `ISessionStore`, `ICredentialStore`, `IGroupStore` or `IApiTokenStore` (namespace `Qodalis.Cli.Plugin.Users.Stores`; the audit store, `IAuditStore`, is the audit plugin's) and register it:

```csharp
cli.AddUsers(options => options
    .SetUserStore<PostgresUserStore>()          // constructed via DI
    .SetSessionStore(sp => new RedisSessionStore(sp.GetRequiredService<IConnectionMultiplexer>())));
```

`GET /users` (`listusers --query --skip --take`) calls `IUserStore.QueryUsersAsync(query, skip, take)`. Its default loads every user through `GetUsersAsync` and pages in memory; a store backed by a database should implement it with a query, oldest users first, `query` matching name or email (case-insensitive):

```csharp
public Task<List<User>> QueryUsersAsync(string? query, int skip, int? take) =>
    db.Users
        .Where(u => query == null || EF.Functions.ILike(u.Name, $"%{query}%") || EF.Functions.ILike(u.Email, $"%{query}%"))
        .OrderBy(u => u.CreatedAt)
        .Skip(skip)
        .Take(take ?? int.MaxValue)
        .Select(u => ToUser(u))
        .ToListAsync();
```

## External auth adapter

The middleware order and a complete setup with the admin dashboard are in [Use with your app's sign-in](../../README.md#use-with-your-apps-sign-in).

If the host application already authenticates requests, provide an `IExternalAuthAdapter` or use the delegate shortcut. The adapter receives the `HttpContext` and returns an `ExternalAuthResult` (user + admin flag) or `null`. Login (`POST /sessions`), logout (`DELETE /sessions/current`), `verify-password` and password changes then answer `501`, no `root` user is seeded (the built-in groups still are), and permissions come from the groups the adapter returns (`IsAdmin` adds `admin`) plus the result's
`Permissions`. Group names missing from the group store grant nothing (and break nothing).

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

The plugin always calls `IExternalAuthAdapter.AuthenticateAsync(HttpContext, CancellationToken)`, so an adapter can
load the user, their roles or their disabled state from a database. Its default implementation calls the synchronous
`Authenticate(HttpContext)`, so existing adapters keep working; an adapter implements either one. The delegate
shortcut has an async form too (the token is `RequestAborted` for requests and upgrades, and is cancelled when the
server stops for a socket re-check):

```csharp
options.UseExternalAuth(async (ctx, cancellationToken) =>
{
    if (await userAccess.CurrentAsync(ctx, cancellationToken) is not { } current) return null;
    return new ExternalAuthResult { User = ToCliUser(current), IsAdmin = current.IsAdmin };
});

// or a DI-constructed adapter implementing AuthenticateAsync
options.UseExternalAuth<HostAuthAdapter>();
```

Hosts with their own roles can grant permissions directly instead of keeping a group per permission:

```csharp
return new ExternalAuthResult
{
    User = ToCliUser(account),                       // Groups may be empty
    Permissions = ["commands:execute", "logs:read"], // added to the groups' permissions
};
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

**Events and logs sockets opened by adapter users** are re-checked the same way: every `SessionCheckInterval`
(`cli.ServerOptions.Shell.SessionCheckInterval`, default 30 s, capped at 60 s; it applies whether or not the shell is
enabled) the adapter runs again on the snapshot of the upgrade or ticket request. `null`, a different user, a
disabled user, an exception, or a user who no longer holds the socket's permission (`logs:read` for the logs socket;
the events socket only needs authentication) closes the socket with `4401 session ended`.

A user the adapter returns with `Disabled = true` is refused (401 `account disabled`).

## Read-only stores

When the host application owns the users or groups (a custom `IUserStore`/`IGroupStore` over its own accounts and
roles), mark them read-only instead of throwing from the store's write methods:

```csharp
cli.AddUsers(options => options
    .UseExternalAuth<HostAuthAdapter>()
    .SetUserStore<HostUserStore>()   // only the read methods need real implementations
    .ReadOnlyUsers()
    .ReadOnlyGroups());
```

- **`ReadOnlyUsers()`** (`UsersReadOnly = true`): `POST /users`, `PUT /users/{idOrName}` (self updates too),
  `DELETE /users/{idOrName}`, `PUT /users/{idOrName}/password` and `POST`/`DELETE /groups/{name}/members/{idOrName}`
  answer `501 { "error": "Users are managed by the host application" }` without calling any store. No `root` user is
  seeded, and deleting a group leaves the users' memberships alone.
- **`ReadOnlyGroups()`** (`GroupsReadOnly = true`): `POST /groups`, `PUT /groups/{name}` and `DELETE /groups/{name}`
  answer `501 { "error": "Groups are managed by the host application" }` without calling the store, and the built-in
  `admin` and `users` groups are not seeded (the host's store provides the groups it needs).
- Reads keep working. `GET /sessions/current` and the login response (`session`) carry `usersReadOnly` and
  `groupsReadOnly` (always present, `false` by default) so clients can hide `adduser`, `groupadd` and the like.

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
