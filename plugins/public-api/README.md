# Qodalis.Cli.Plugin.PublicApi

Public API plugin for the Qodalis CLI Server. It turns the host's own routes into a public API:

- **A guard** (`.RequirePublicApi("rates:read")`, `[RequirePublicApi("rates:read")]`) that accepts only `host` API
  tokens (`qcli_pat_…`, created by the host with `IApiTokenManager`), checks a permission and the caller's quota.
- **Quotas per account**: requests per UTC day and per UTC minute, summed over every token of the account (revoked
  ones included), with `X-RateLimit-*` headers.
- **Usage per token and day**, shown as `usage` on the users plugin's token views (`GET /api/v1/qcli/tokens`, the
  dashboard) and through `IPublicApiUsage` for the host's own account pages.
- **The public OpenAPI document** at `/qcli/api/openapi.json` (the host's `AddOpenApi("public")` document with the
  bearer scheme and each route's permission) and **Swagger UI** at `/qcli/api/swagger`.
- **The portal's API section**: with the portal plugin, the API reference appears on the portal site.

It needs the users plugin, which authenticates the tokens.

> **Requires Microsoft.OpenApi 2.** The package depends on `Microsoft.AspNetCore.OpenApi` 10, which brings
> Microsoft.OpenApi 2.x. A host that also uses Swashbuckle needs Swashbuckle 10 or later: older Swashbuckle versions
> are built on Microsoft.OpenApi 1.x and fail at run time next to it.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.PublicApi
```

## Quick start

```csharp
using Qodalis.Cli.Plugin.PublicApi;
using Qodalis.Cli.Plugin.Users.Authorization;
using Qodalis.Cli.Plugin.Users.Extensions;

builder.Services.AddOpenApi("public", o => o.ShouldInclude = d => d.GroupName == "public");
builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        cli.AddPermissions(p => p.Group("Rates")
            .Add("rates:read", "Official exchange rates")
            .Add("banks:read", "Banks and their buy and sell rates"));
        cli.AddUsers();
        cli.AddPublicApi(o => o.UseQuotaResolver<PlanQuotaResolver>());
    });

var app = builder.Build();
app.UseRouting();
app.UseQodalisUsers();
app.UseQodalisPublicApi(); // after UseRouting, before the endpoints
app.MapControllers();

app.MapGet("/v1/rates", (HttpContext ctx, RatesService rates) => rates.Today(ctx.GetQcliCaller()!.UserId))
    .RequirePublicApi("rates:read")
    .WithGroupName("public")
    .WithTags("Rates");
```

The host creates the tokens for its users (an account page, a sign-up flow):

```csharp
var created = await tokens.CreateAsync(userId, new ApiTokenCreateOptions
{
    Name = "rates site",
    Audience = ApiTokenAudience.Host,
    Permissions = ["rates:read"],
});
// created.Token is shown once
```

## Options

`cli.AddPublicApi(Action<PublicApiOptions>? configure = null)`

| Option | Default | Description |
|---|---|---|
| `BasePath` | `/qcli/api` | Where `openapi.json` and `swagger` are served; starts with `/`, no trailing `/`, not `/` (else `ArgumentException("invalid public-api base path: <path>")`) |
| `DocumentName` | `public` | The `services.AddOpenApi(name)` document that is published |
| `Title` | `API reference` | The API section's label on the portal (the document keeps its own `info.title`) |
| `ServerUrl` | the request's `scheme://host[pathBase]` | The document's `servers` when the host's document has none. **Set it in production**: otherwise the request's `Host` header ends up in the published document |
| `UsageRetentionDays` | `31` | Days of per-day counters kept (token views show 30) |
| `UseQuotaResolver<T>()` | none (unlimited) | An `IPublicApiQuotaResolver`, registered scoped and resolved from the request's services |
| `UseQuotaResolver((caller, services, ct) => ...)` | none | A delegate resolver; `services` is the request's provider |
| `SetUsageStore<T>()` / `SetUsageStore(sp => ...)` | in memory | The usage store (see [Several processes](#several-processes)) |

## The guard

| API | |
|---|---|
| `endpoint.RequirePublicApi(string? permission = null)` | Minimal APIs and route groups |
| `[RequirePublicApi("rates:read")]` | MVC controllers and actions |

Checks, in order; the first failure answers `{ "error": "<text>" }` and nothing is counted:

| # | Condition | Answer |
|---|---|---|
| 1 | authentication fails | 401 `unauthorized`, 401 `invalid or expired token`, 403 `account disabled`, 403 `password change required` |
| 2 | not a `host` token (a session, an external-adapter caller, a `cli` token) | 403 `forbidden: a host token is required` |
| 3 | the permission is not held | 403 `forbidden: missing permission <permission>` |
| 4 | the quota resolver throws | 500 `internal server error` (logged; the error text is never sent) |
| 5 | a limit is `0` (or negative) | 403 `forbidden: no API access` |
| 6 | the account's requests today ≥ `RequestsPerDay` | 429 `quota exceeded: <n> requests per day`, `Retry-After` = seconds to the next UTC midnight |
| 7 | the account's requests this minute ≥ `RequestsPerMinute` | 429 `too many requests: <n> per minute`, `Retry-After` = seconds to the next UTC minute |

Otherwise the request is counted on its token's day and minute counters and the handler runs. The handler reads the
caller with `HttpContext.GetQcliCaller()` (`QcliCaller(UserId, UserName, TokenId, Audience, Permissions)`) and, with
the users plugin's `SetHostUser` (default), `HttpContext.User`.

**Headers.** Every answer of a guarded route that passed the permission check (the handler's own answers and the 429s;
not 401, 403 or 500) carries `X-RateLimit-Limit`, `X-RateLimit-Remaining` and `X-RateLimit-Reset` (seconds until the
window resets, at least 1). They describe the daily limit when there is one, otherwise the per-minute limit, and are
not sent when both are unlimited. A 429 has `X-RateLimit-Remaining: 0` and `Retry-After`.

**Stacked guards.** A route group with `.RequirePublicApi()` and an endpoint in it with `.RequirePublicApi("rates:read")`
(or `[RequirePublicApi]` on a controller and on its action) count the request once. The first guard to run checks
every public-api guard's permission on the endpoint before counting, so a request the stricter guard refuses (403
`forbidden: missing permission <p>`) is not counted; the others only check their own permission.

**Store failures.** When the usage store fails after the request was counted (a later increment or the account read
throws), the counts already taken are taken back (best effort) and the request answers 500 `internal server error`.

**Only `RequirePublicApi` routes are counted.** A route guarded with `.RequireQcliAuthorization(..., hostTokenOnly:
true)` or `[QcliAuthorize(..., HostTokenOnly = true)]` accepts only host tokens and appears in the public document with
its permission, but has no quota and no usage. Use `RequirePublicApi` for every route that should be counted.

Windows are fixed UTC days (`yyyy-MM-dd`) and minutes, whatever the server's time zone. Revoking a token and creating
a new one does not reset the quota: the account's count includes the revoked token's requests.

## Quotas

```csharp
public sealed class PlanQuotaResolver(AppDbContext db) : IPublicApiQuotaResolver
{
    public async ValueTask<PublicApiQuota> GetQuotaAsync(QcliCaller caller, CancellationToken ct)
    {
        var plan = await db.Accounts.Where(a => a.UserId == caller.UserId).Select(a => a.Plan).SingleOrDefaultAsync(ct);
        return plan switch
        {
            "pro" => new PublicApiQuota(RequestsPerDay: 100_000, RequestsPerMinute: 600),
            "free" => new PublicApiQuota(1_000, 60),
            _ => new PublicApiQuota(0, null), // 403 forbidden: no API access
        };
    }
}
```

`PublicApiQuota(long? RequestsPerDay, long? RequestsPerMinute)`: `null` = no limit of that kind;
`PublicApiQuota.Unlimited` = both.

## Usage

With the plugin added, every token view of the users plugin (`GET /api/v1/qcli/tokens`, `?all=true`,
`IApiTokenManager.ListAsync`) ends with:

```json
"usage": { "today": 12, "last30Days": 340, "days": [ { "date": "2026-10-21", "requests": 0 }, "… 30 entries, the last one today (UTC)" ] }
```

For the host's own pages:

```csharp
PublicApiAccountUsage usage = await app.Services.GetRequiredService<IPublicApiUsage>().GetAccountUsageAsync(userId);
// usage.Today, usage.Quota (from the resolver), usage.Tokens: (TokenId, Name, Usage) per current token
```

An unknown user throws `UsersApiException` 404 `user not found`.

## OpenAPI document and Swagger UI

`app.UseQodalisPublicApi()` serves:

| Route | Answer |
|---|---|
| `GET <BasePath>/openapi.json` | The document (OpenAPI 3.1), `Cache-Control: no-cache`, `Access-Control-Allow-Origin: *`; anonymous, credentials ignored |
| `GET <BasePath>/swagger`, `<BasePath>/swagger/` | Swagger UI for that document only (no persisted authorization, "try it out" on, titled with `info.title`) |

The document is the host's `AddOpenApi(DocumentName)` document. The plugin adds, only where the host has not:

- `components.securitySchemes.qcliToken` (HTTP bearer, format `qcli_pat`) and a top-level `security` on it;
- `servers` (`ServerUrl`, or the request's URL);
- `x-required-permission` and `x-host-token-only: true` on each operation guarded by `RequirePublicApi` or by a users
  guard (`[QcliAuthorize]`, `.RequireQcliAuthorization(...)`; `x-host-token-only` with `HostTokenOnly`).
  `x-required-permission` is always one string (the contract's shape, which the portal reads): with stacked guards
  that name different permissions (a route group's and its endpoint's), it is the most specific one, the endpoint's.
  Every one of them is still required; document the others in the operation's description if callers need them.

**Calling the API from the portal ("try it").** On the same origin nothing is needed. When the portal or Swagger UI
runs on another origin than the API, the host's API needs CORS for that origin (with the `Authorization` header
allowed) and `Access-Control-Expose-Headers: X-RateLimit-Limit, X-RateLimit-Remaining, X-RateLimit-Reset, Retry-After`
so the browser shows the quota headers. `openapi.json` itself is served with `Access-Control-Allow-Origin: *`.

A host's own `x-codeSamples` pass through untouched; the portal site generates code samples itself. Without the named
document, `UseQodalisPublicApi()` throws `InvalidOperationException("public-api: no OpenAPI document named 'public';
call services.AddOpenApi(\"public\")")`.

## Several processes

The default store keeps the counters in memory: one process, lost on restart. To share them between processes (and
keep usage across restarts), use [Qodalis.Cli.Plugin.PublicApi.EfCore](../public-api-efcore/README.md):

```csharp
cli.AddEfCorePublicApiUsageStore(o => o.UseNpgsql(connectionString));
```

User ids up to 128 characters (the `qcli_api_usage.UserId` column, as `api_tokens.UserId`); the guard prunes old
counters at most once an hour with one delete over the table, which is fine at the table's size (31 days of rows per
token).

In one process exactly the limit is admitted, also under parallel requests. Across processes the store's
increment-then-check-then-compensate never admits more than the limit; under contention a request may be refused
while the account is just below it.

## A second service that only serves the API

A service that serves the public API next to the main one (sharing its database) needs no CLI routes and no users
routes of its own:

```csharp
builder.Services.AddOpenApi("public", o => o.ShouldInclude = d => d.GroupName == "public");
builder.Services.AddControllers().AddCli(cli =>
{
    cli.WithoutCliRoutes();
    cli.AddPermissions(p => p.Group("Rates").Add("rates:read", "Official exchange rates"));
    cli.AddUsers(o => o.UseGuardOnly());
    cli.AddEfCoreApiTokenStore(o => o.UseNpgsql(connectionString));
    // users and groups: the stores the main service uses (o.SetUserStore<...>(), o.SetGroupStore<...>())
    cli.AddPublicApi(o => o.UseQuotaResolver<PlanQuotaResolver>());
    cli.AddEfCorePublicApiUsageStore(o => o.UseNpgsql(connectionString));
});
```

Tokens must be created and revoked from one service only: the users plugin checks token names and the 20-token limit
in the process that creates the token. Every service may validate tokens over the shared stores.

## License

MIT
