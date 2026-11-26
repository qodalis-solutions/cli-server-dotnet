# Qodalis.Cli.Plugin.Admin

Admin dashboard plugin for [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet). Provides a web-based admin UI with JWT authentication, server status monitoring, log viewing, plugin management, and WebSocket client tracking.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.Admin
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.Admin.Extensions;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddAdmin();
});

// After building the app:
app.UseQodalisAdmin();
```

The dashboard UI is served at `/qcli/admin` and all API endpoints are mounted under `/api/v1/qcli/`.

## Configuration

Configure via `AddAdmin()` or environment variables:

```csharp
cli.AddAdmin(config =>
{
    config.Username = "admin";
    config.Password = "s3cret";
    config.JwtExpiry = TimeSpan.FromHours(8);
    config.DashboardPath = "/path/to/dashboard/dist";
    config.Theme.Mode = AdminThemeMode.System; // see "Color scheme" below
    config.SignInUrl = "https://app.example.com/sign-in"; // see "Sign in with the host app's session"
    config.Marketplace.Install = "verified"; // see "Dashboard plugins from the marketplace"
});
```

| Environment Variable | Default | Description |
|---|---|---|
| `QCLI_ADMIN_USERNAME` | `admin` | Admin login username |
| `QCLI_ADMIN_PASSWORD` | `admin` | Admin login password |
| `QCLI_ADMIN_JWT_SECRET` | Auto-generated | Secret key for signing JWT tokens |

## API Endpoints

| Endpoint | Method | Description |
|---|---|---|
| `/api/v1/qcli/auth/mode` | GET | Public. Which sign-in the dashboard shows: `{ "mode": "password" \| "external", "signInUrl": string \| null }` |
| `/api/v1/qcli/auth/login` | POST | Authenticate and receive a JWT token (501 in external mode) |
| `/api/v1/qcli/auth/me` | GET | Get current authenticated user info |
| `/api/v1/qcli/status` | GET | Server status (uptime, process memory `memoryUsageMb`, the machine's memory `systemMemoryTotalMb`, `systemMemoryUsedMb`, `memoryLimited`, CPU, features) |
| `/api/v1/qcli/status/history` | GET | The status sampled every 15 seconds (process memory, the machine's used and total memory, CPU % of one core, WebSocket clients, shell sessions), oldest first: `minutes` (default 60, 1..1440). Kept in memory for 24 hours |
| `/api/v1/qcli/config` | GET | `{ defaultStore, providers }`: the store providers use unless they have their own, and the server's [config providers](../../README.md#config-providers) the caller may see (`config:read`), each stored one with its `store` |
| `/api/v1/qcli/config/{provider}` | GET | One provider |
| `/api/v1/qcli/config/{provider}/{section}` | PATCH | `{ changes, version? }` (`config:write`): 200 with the section; 400 per-key errors; 409 with the current version |
| `/api/v1/qcli/logs` | GET | Query log entries: `level` (comma list), `search`, `service`, `from`, `to`, `limit`, `offset`. The latest 1000 entries of this process, or Seq with [`Qodalis.Cli.Plugin.Logs.Seq`](../logs-seq/README.md) (any `ILogStore`) |
| `/api/v1/qcli/logs/histogram` | GET | Entries per level (`DEBUG`, `INFO`, `WARN`, `ERROR`) in `buckets` equal columns (default 24, 1..200) of `[from, to)` (default the last 24 hours), with the filters of `/logs`. Counts the buffer, or asks Seq to count |
| `/api/v1/qcli/plugins` | GET | List registered plugin modules |
| `/api/v1/qcli/plugins/installed` | GET | List the installed server plugin packages (`id`, `name`, `version`, `description`) |
| `/api/v1/qcli/plugins/{name}/toggle` | POST | Enable or disable a plugin module |
| `/api/v1/qcli/ws/clients` | GET | List active WebSocket connections |
| `/api/v1/qcli/dashboard/extensions` | GET | The dashboard's pages (`{ extensions }`), read on every request; the marketplace's plugins carry `source: "marketplace"` |
| `/api/v1/qcli/dashboard/marketplace/*` | * | Dashboard plugins from the marketplace (see [Marketplace API](#marketplace-api)) |

`systemMemoryTotalMb` is the RAM the server can use: the container (cgroup) limit when one is set below the installed RAM
(`memory.max`, v1 `memory.limit_in_bytes`, else the GC's container-aware `TotalAvailableMemoryBytes`; then `memoryLimited`
is true), otherwise the installed RAM. `systemMemoryUsedMb` is the memory in use: the container's usage when limited
(`memory.current`, v1 `memory.usage_in_bytes`), otherwise total minus available (`MemAvailable` of `/proc/meminfo` on
Linux, `GlobalMemoryStatusEx` on Windows, free, file-backed, purgeable and speculative pages on macOS). Each is null when
it cannot be read; register a `SystemMemoryReaders` to read them elsewhere.

The plugin registers two config providers: `auth` (read-only: the sign-in mode, the static admin's username, whether
the default credentials or an explicit JWT secret are in use, the token lifetime and the sign-in URL; never a password or
the secret) and `logs` (the ring buffer's size, 100 to 100000 entries, and its lowest level, changeable at runtime).
The `logs` changes are kept in the server's default config store unless the plugin gets its own,
`cli.AddAdmin(o => o.LogsConfigStore = store)`; the host's store for the id (`cli.SetConfigStore(store, "logs")`) still
wins.

Every log line the ring buffer captures is also published on the server's event bus (`ICliEventBus`) as `log:entry`
(`timestamp`, `level`, `message`, `source`; audience `logs:read`), which the dashboard's Logs page follows live.

## Authentication

Login is rate-limited to 5 attempts per IP per minute. Successful login returns a JWT token that must be included as a `Bearer` token in subsequent requests to protected admin endpoints.

With the users plugin installed, the dashboard signs in through it (sessions, lockout, audit) and needs the
`admin:access` permission; the static `QCLI_ADMIN_USERNAME`/`QCLI_ADMIN_PASSWORD` are only used without it.

### Sign in with the host app's session

The full setup (adapter, middleware order, permissions) is in [Use with your app's sign-in](../../README.md#use-with-your-apps-sign-in).

When the users plugin delegates authentication to your application (`UseExternalAuth`), the dashboard runs in
**external** mode:

- `GET /api/v1/qcli/auth/mode` answers `{ "mode": "external", "signInUrl": "<SignInUrl>" }`. It is public, also on
  protected servers, so the dashboard can call it before anyone signs in. Otherwise it answers `"password"`.
- The dashboard hides its username/password form and shows "Sign in at `<signInUrl>`" with a Retry button (or tells
  users to sign in through the host application when `SignInUrl` is not set). Any 401 leads to that screen.
- `POST /api/v1/qcli/auth/login` signs no one in: it answers `501` with `code: "EXTERNAL_AUTH"`, a message pointing
  to the host's sign-in and `signInUrl`. The static admin credentials are never accepted in this mode.

`SignInUrl` must be an absolute `http`/`https` URL or a root-relative path (`/sign-in`); anything else (for example
`javascript:` or `//host`) fails at startup.

**Handoff.** Send signed-in users to the dashboard with your bearer token in the URL fragment:

```
/qcli/admin/#token=<host bearer token>
```

A fragment is never sent to servers or written to access logs. The dashboard reads it on startup, stores it as its
admin token, removes it from the address bar (`history.replaceState`) and sends it as `Authorization: Bearer …` on
every API call, where your external auth adapter validates it like any other request from your app.

```csharp
// Host app: the adapter accepts the app's own JWT.
cli.AddUsers(users => users.UseExternalAuth(context =>
{
    var principal = context.User; // populated by app.UseAuthentication() from the Bearer header
    if (principal.Identity?.IsAuthenticated != true) return null;
    return new ExternalAuthResult
    {
        User = new User { Id = principal.FindFirst("sub")!.Value, Name = principal.Identity.Name!, Email = principal.FindFirst("email")?.Value ?? "" },
        IsAdmin = principal.IsInRole("admin"),
    };
}));
cli.AddAdmin(o => o.SignInUrl = "/sign-in?returnUrl=%2Fadmin%2Fconsole");
```

```ts
// Host front end: an "Open console" button for a signed-in admin.
window.location.href = `/qcli/admin/#token=${encodeURIComponent(accessToken)}`;
```

When the token expires, the next API call answers 401 and the dashboard shows the sign-in screen; after signing in
again, the host repeats the handoff.

## Color scheme

Match the dashboard to your own design with `Theme`:

```csharp
cli.AddAdmin(o =>
{
    o.Theme.Mode = AdminThemeMode.System;
    o.Theme.Light = new AdminThemeColors
    {
        Primary = "#2a6bba", PrimaryHover = "#1f5596", Accent = "#2a6bba",
        Background = "#f1f6fa", BackgroundAccent = "#f1f6fa", Surface = "#ffffff",
        Border = "#d9e3ec", Heading = "#142231", Text = "#142231", Muted = "#5d6f82",
    };
    o.Theme.Dark = new AdminThemeColors
    {
        Primary = "#4f9be0", PrimaryHover = "#3f8ad3", OnPrimary = "#0d1823", Accent = "#4f9be0",
        Background = "#0d1823", BackgroundAccent = "#0d1823", Surface = "#13212f", Popover = "#13212f",
        Border = "#22364a", Text = "#e6eef6", Muted = "#93a6b9",
    };
});
```

`AdminTheme`, `AdminThemeColors` and `AdminThemeMode` live in `Qodalis.Cli.Plugin.Admin.Services`; each token is a
PascalCase property (`PrimaryHover`) and is written camelCase (`primaryHover`) to `theme.json`. `UseQodalisAdmin()`
throws an `ArgumentException` on an invalid theme.

`mode` is `dark` (default), `light`, or `system` to follow the browser's `prefers-color-scheme`. Each scheme takes any
subset of the tokens below; the ones you leave out keep their defaults.

Values must be plain CSS colors: hex (`#rgb`, `#rgba`, `#rrggbb`, `#rrggbbaa`), a color function with numeric arguments
(`rgb()`, `rgba()`, `hsl()`, `hsla()`, `hwb()`, `lab()`, `lch()`, `oklab()`, `oklch()`) or a named color. Anything else
(an unknown mode or token, `var()`, `url()`, `;` ...) is rejected at startup with an error naming the token, so a theme
cannot inject CSS.

The plugin serves the validated scheme at `GET /qcli/admin/theme.json` (public, like the dashboard files, since the
sign-in page uses it), e.g. `{"mode":"system","dark":{"primary":"#4f9be0"},"light":{"primary":"#2a6bba"}}`. The
dashboard applies it before its first render.

The defaults match the Qodalis CLI panel: its grays, indigo accent with the violet glow, and status colors in dark; the same palette for light backgrounds in light.

| Token | CSS variable | Used for | Dark default | Light default |
| --- | --- | --- | --- | --- |
| `primary` | `--qcli-primary` | Primary buttons, active navigation, toggles, focus rings | `#818cf8` | `#4f46e5` |
| `primaryHover` | `--qcli-primary-hover` | Primary buttons on hover | `#a5b4fc` | `#4338ca` |
| `onPrimary` | `--qcli-on-primary` | Text and icons on `primary` | `#111827` | `#ffffff` |
| `accent` | `--qcli-accent` | Highlighted text: links, command names, active items | `#a78bfa` | `#7c3aed` |
| `background` | `--qcli-background` | Page background | `#111827` | `#f9fafb` |
| `backgroundAccent` | `--qcli-background-accent` | Start of the page background gradient (set it to `background` for a flat page) | `#1f2937` | `#eef2ff` |
| `sidebar` | `--qcli-sidebar` | Sidebar background | `#1f2937` | `#ffffff` |
| `surface` | `--qcli-surface` | Cards, panels and tables | `rgba(255, 255, 255, 0.04)` | `#ffffff` |
| `popover` | `--qcli-popover` | Dialogs and dropdown menus (opaque) | `#1f2937` | `#ffffff` |
| `control` | `--qcli-control` | Inputs, secondary buttons, chips | `rgba(255, 255, 255, 0.06)` | `rgba(17, 24, 39, 0.05)` |
| `controlHover` | `--qcli-control-hover` | Inputs and secondary buttons on hover | `rgba(255, 255, 255, 0.1)` | `rgba(17, 24, 39, 0.1)` |
| `border` | `--qcli-border` | Borders and dividers | `#374151` | `#e5e7eb` |
| `overlay` | `--qcli-overlay` | Backdrop behind dialogs | `rgba(0, 0, 0, 0.6)` | `rgba(17, 24, 39, 0.45)` |
| `heading` | `--qcli-heading` | Headings and values | `#ffffff` | `#111827` |
| `text` | `--qcli-text` | Body text | `rgba(255, 255, 255, 0.87)` | `#1f2937` |
| `muted` | `--qcli-muted` | Secondary text and labels | `rgba(255, 255, 255, 0.6)` | `#4b5563` |
| `subtle` | `--qcli-subtle` | Hints, placeholders, timestamps | `rgba(255, 255, 255, 0.4)` | `#6b7280` |
| `success` | `--qcli-success` | Success states | `#3fb950` | `#1a7f37` |
| `warning` | `--qcli-warning` | Warning states | `#f0883e` | `#bc4c00` |
| `error` | `--qcli-error` | Errors and destructive actions | `#f85149` | `#cf222e` |
| `info` | `--qcli-info` | Informational states | `#58a6ff` | `#0969da` |

## Command modules

The Plugins page also lists the command modules, each with its commands and a switch that disables them
(`/api/v1/qcli/plugins`). A module added with `cli.AddModule(...)` is listed with its name, version, description and
`Icon` (one emoji or an image URL). Commands registered on their own (`cli.AddProcessor<T>()`, built by dependency
injection) are listed under the module their `Module` property names. To describe such a group, add a module of the
same name with no processors of its own:

```csharp
public sealed class ShopModule : CliModule
{
    public override string Name => "shop";
    public override string Version => "1.0.0";
    public override string Description => "Shop administration";
    public override string? Icon => "🛒";
    public override IEnumerable<ICliCommandProcessor> Processors => []; // StatsCommand etc. set Module = "shop"
}

cli.AddModule(new ShopModule())
   .AddProcessor<StatsCommand>()
   .AddProcessor<OrdersCommand>();
```

## Installed plugins

The dashboard's Plugins page lists the server plugins the host installed: the core server, and each plugin package
added through its `Add*` method (`AddUsers`, `AddAudit`, `AddAdmin`, `AddJob`, `AddFileSystem` with its storage provider,
`AddDataExplorerProvider`, `AddEfCoreJobStorage`, `AddEfCoreAuditStore`, modules from other packages), with the
package's version and description. A plugin of your own lists itself from its builder extension:

```csharp
public static CliBuilder AddBilling(this CliBuilder cli)
{
    cli.AddInstalledPlugin(typeof(BillingModule).Assembly);
    // ... register the plugin's services
    return cli;
}
```

Each package shows its icon (`icon`: one emoji or an image URL), declared in the plugin's project as assembly
metadata; the dashboard shows a default icon for a package without one:

```xml
<ItemGroup>
  <AssemblyMetadata Include="QodalisCliIcon" Value="💳" />
</ItemGroup>
```

The status reports the `audit` feature when the users plugin is installed, which shows the dashboard's Audit page.

## Name and icon

By default the dashboard is "Qodalis Cli Admin" with the Qodalis Solutions logo. An application that ships it
under its own name sets `Branding` (`AdminBranding`, in `Qodalis.Cli.Plugin.Admin.Services`):

```csharp
cli.AddAdmin(o =>
{
    o.Branding.Name = "RateBell";          // browser tab, sidebar, sign-in page
    o.Branding.IconUrl = "/favicon.svg";   // http(s) URL, root-relative path or base64 data:image URL
    o.Branding.CliName = "RateBell CLI";   // the terminal's welcome banner
});
```

Anything left unset keeps the Qodalis default. The terminal's copyright line, docs link and `version` stay Qodalis
CLI's. The values are served with the color scheme at `/qcli/admin/theme.json` and validated at startup.

## Dashboard plugins from the marketplace

A *dashboard plugin* is an npm package whose bundle is a dashboard extension (`extension.js`, optional
`extension.css`, built with `@qodalis/cli-server-dashboard-kit`'s `defineExtension`), listed in the
[plugin marketplace](https://plugins.qodalis.com) with `kind: "dashboard"`. The admin plugin lists them, installs,
updates and removes them at runtime, and serves them from the server's own origin. A plugin runs inside the dashboard
with the signed-in user's rights, so by default only verified publishers' plugins can be installed. The marketplace is
on by default.

```csharp
cli.AddAdmin(config =>
{
    config.Marketplace.Url = "https://plugins.qodalis.com"; // default
    config.Marketplace.Install = "verified"; // default; "listed": any listed version; "off": browse only
    config.Marketplace.Dir = "/var/lib/myapp/dashboard-plugins"; // default: <data dir>/dashboard-plugins
});
```

| `AdminConfig.Marketplace` | Default | Description |
|---|---|---|
| `Enabled` | `true` | `false` serves none of the routes below, leaves out `admin dashboard` and loads no installed plugin |
| `Url` | `https://plugins.qodalis.com` | The marketplace (its public API at `/api/v1/qcli/marketplace`) |
| `Install` | `verified` | `verified`: listed versions of verified publishers only; `listed`: any listed version; `off`: browse only |
| `Dir` | `<data dir>/dashboard-plugins` | Where installed plugins are kept: the server's data directory (`cli.ConfigureServerPlugins(o => o.DataDir = ...)`, else `QCLI_DATA_DIR`, else `qcli-data` in the working directory), so one option moves every folder; an explicit `Dir` wins (add `qcli-data/` to your `.gitignore`) |
| `HttpHandler` | a `SocketsHttpHandler` | The `HttpMessageHandler` for the marketplace and the CDNs (a proxy, or a fake in tests) |
| `Cdns` | jsDelivr, unpkg | Where bundles are downloaded from, in order |
| `Timeout` | 15 s | Timeout of each request |

The folders must be writable. When one cannot be created or written (a container running as a non-root user with the
default `qcli-data` in a read-only app folder), the plugin logs one WARNING at startup naming it, and installing,
updating or removing a dashboard or server plugin answers 500 with the folder and what to set:
`{ "error": "cannot write the plugins folder /app/qcli-data/dashboard-plugins: permission denied (EACCES). Set DataDir (or QCLI_DATA_DIR) to a writable folder" }`
(with an explicit `Dir`: `Set the marketplace's Dir to a writable folder`). `admin dashboard install` and `admin
plugins install` print it as `Error: ...`. Only the folder and the error code are named.

An invalid `Url` or `Install` throws `ArgumentException` (`invalid marketplace url: ...`,
`invalid marketplace install policy: ...`) when the server starts.

**Installing** reads the plugin from the marketplace (`GET /plugins/{name}`) and takes the newest listed version this
dashboard runs (`dashboardApi` 1) unless one is given, checks the trust policy, downloads each file of the version
record (`files: [{ path, integrity, size }]`) from jsDelivr, then unpkg (5 MB at most each), and checks its bytes
against the marketplace's `sha384-` integrity before anything is written: a mismatch on every CDN is a 502 and stores
nothing. The files are stored as `<dir>/<name>/<version>/extension.js|css` (a scoped name `@scope/name` as the
directory `@scope+name`; names and versions are validated, so nothing leaves `Dir`) and listed in
`<dir>/installed.json`, written atomically (a temporary file moved over it, mode 0600):

```json
{
  "plugins": [
    {
      "name": "@qodalis/cli-dashboard-module-jobs",
      "version": "1.1.0",
      "id": "jobs",
      "title": "Jobs",
      "icon": "⏱️",
      "files": [
        { "file": "extension.js", "path": "dist/extension.js", "integrity": "sha384-…", "size": 48211 },
        { "file": "extension.css", "path": "dist/extension.css", "integrity": "sha384-…", "size": 5120 }
      ],
      "installedAt": "2026-11-25T10:04:05.123Z",
      "installedBy": "admin",
      "config": { "entries": [{ "key": "refreshSeconds", "type": "number", "default": 30, "validation": { "min": 5, "max": 3600 } }] }
    }
  ]
}
```

`config` (the installed version's settings, see [Settings of dashboard plugins](#settings-of-dashboard-plugins)) is
absent for a plugin without settings.

The plugin is then registered as a dashboard extension at once (no restart; `CliDashboardExtensions.Add` and `Remove`
change the pages at runtime): its page id is the package's suffix for `@qodalis/cli-dashboard-module-<id>`, else the
extension's own id (`extension.id` of the version record, then of the plugin), else the package name without its scope
and `cli-dashboard-` prefix. An id another extension already has is a 409. Its files are served at
`/qcli/modules/<id>/extension.js|css` (no sign-in, `Cache-Control: no-cache` with an ETag, the entry carrying
`?v=<hash>`), and `GET /dashboard/extensions` lists it with `source: "marketplace"`. An update keeps the page id and
deletes the previous version's files (a `PUT` to the installed version answers 200 and changes nothing); a removal
unregisters the page and deletes the files. Changes run one at a time; `installedBy` is the signed-in user, or
`anonymous` for a terminal command on a server without sign-in.

At startup the plugin registers what `installed.json` lists, without the marketplace; a plugin whose files are missing
or no longer match their integrity, or whose id another extension took, is skipped with a warning.

Every change is audited (`dashboard.plugin.install`, `dashboard.plugin.update`, `dashboard.plugin.remove`: target the
package, `properties: { name, version, id, previousVersion?, config?, configDropped? }`, the keys of the settings given
and dropped) in the server's `IAuditStore` (the audit or users plugin) and published on the event bus as
`dashboard:extensions-changed` (`{ action, name, version, id, previousVersion? }`, audience `admin:access`), so open
dashboards reload their pages.

### Settings of dashboard plugins

A dashboard plugin may declare settings with defaults, in the config providers' entry model: `qodalis.config.entries`
of its package.json, published by the marketplace as `config: { entries }` of the version record:

```json
{
  "entries": [
    { "key": "refreshSeconds", "label": "Refresh every", "type": "number", "default": 30, "validation": { "min": 5, "max": 3600 } },
    { "key": "columns", "type": "string[]", "default": ["name", "status"] },
    { "key": "mode", "type": "enum", "default": "compact", "validation": { "options": ["compact", "full"] } }
  ]
}
```

Types `string`, `number`, `boolean`, `string[]`, `enum`, `json`, `duration` and `url`, **never `secret`** (a dashboard
plugin runs in the browser: it cannot keep one). Keys match `^[a-zA-Z][a-zA-Z0-9_.-]{0,63}$`, at most 50 entries, an
`enum` has `validation.options`, every default passes its own validation, at most 32 KB of JSON. The marketplace
rejects versions breaking these rules; the server checks them again (`DashboardPluginConfigs.Parse`), and a version
whose settings fail installs **without settings**, the response carrying
`"warnings": ["the settings of <name>@<version> are invalid and were ignored: <reason>"]`. Settings missing from the
details' versions are read from the version record.

The admin plugin registers the config provider **`dashboard-plugins`** ("Dashboard plugins", writable,
`DashboardPluginsConfigProvider`, `DashboardMarketplace.Config`) when the server starts, unless the marketplace is off
or the host registered a provider with that id. It has **one section per installed plugin with settings**: the section
id is the page id, its title the plugin's title, its entries the installed version's with their defaults. A plugin
without settings gets no section. Its store is resolved like every provider's: the host's store for
`dashboard-plugins` (`cli.SetConfigStore(store, "dashboard-plugins")`, `SetConfigStore<TStore>("dashboard-plugins")`,
`ConfigureConfig(o => o.Stores["dashboard-plugins"] = store)`), else the default store, else memory.
`GET /api/v1/qcli/config/dashboard-plugins`:

```json
{
  "id": "dashboard-plugins",
  "title": "Dashboard plugins",
  "description": "Settings of the dashboard plugins installed from the marketplace",
  "order": 85,
  "writable": true,
  "storage": { "kind": "store", "description": "Changes are saved to its config store", "sources": [] },
  "store": { "kind": "file", "title": "JSON file", "detail": "qcli-config.json", "persistent": true },
  "sections": [
    {
      "id": "tickets",
      "title": "Tickets",
      "description": "@acme/cli-dashboard-tickets@1.0.0",
      "mutable": true,
      "version": "1",
      "allowCustomKeys": false,
      "entries": [
        { "key": "refreshSeconds", "label": "Refresh every", "description": "", "type": "number", "value": 60, "default": 30, "source": "file", "mutable": true, "requiresRestart": false, "sensitive": false, "validation": { "min": 5, "max": 3600 } },
        { "key": "columns", "label": "columns", "description": "", "type": "string[]", "value": ["name", "status"], "default": ["name", "status"], "source": "default", "mutable": true, "requiresRestart": false, "sensitive": false },
        { "key": "mode", "label": "mode", "description": "", "type": "enum", "value": "compact", "default": "compact", "source": "default", "mutable": true, "requiresRestart": false, "sensitive": false, "validation": { "options": ["compact", "full"] } }
      ]
    }
  ]
}
```

Settings are changed like any other: `PATCH /api/v1/qcli/config/dashboard-plugins/<id>` (`config:write`), `config set
dashboard-plugins.<id>.<key> <value>`, MCP `config_set`; the same validation (400 with per-key errors), versions (409),
audit (`config.update`) and `config:changed` event.

- **Install**: `POST /installed` takes `config: { key: value }`, checked against the version's settings **before
  anything is downloaded or stored** (400 `{ "error": "validation failed", "errors": { "refreshSeconds": "must be at
  least 5", "nope": "unknown setting" } }`; `config must be an object` for anything else) and stored with the install
  (`null` keeps the default). Values left in the store for that page id by an earlier install are cleared.
  Install-time values need `dashboard:install`, not `config:write`.
- **Update**: `PUT /installed/{name}` takes `config` too, checked against the new version's settings. Stored values
  whose key the new version still has and whose value it still accepts are kept, the others deleted and listed in the
  response as `configDropped` (`[]` when none); a value given for a key replaces it, `null` resets it. The same version
  with `config` applies the values. A version without settings removes the section.
- **Remove**: deletes the section and every value stored for it.
- **Restart**: the sections are re-created from `installed.json` (it keeps each installed version's `config`), so
  they come back offline with their stored values; settings there that no longer pass the rules are left out with a
  warning.
- Changing stored values publishes `config:changed` (`{ provider: "dashboard-plugins", section, keys, version,
  requiresRestart: false, store, user }`, audience `config:read`).

The page reads its values from `GET /installed/{name}/config` (`admin:access`): the stored values over the defaults
and the section's version, `{ "values": {}, "version": null }` for a plugin without settings, 404 when not installed:

```json
{ "values": { "refreshSeconds": 60, "columns": ["name", "status"], "mode": "compact" }, "version": "1" }
```

### Marketplace API

Mounted at `/api/v1/qcli/dashboard/marketplace` (`DashboardMarketplaceController`). Reading needs `admin:access`;
`POST`, `PUT` and `DELETE` need `dashboard:install` (core catalog, group Server; the static admin of password sign-in
holds every permission). Responses are `Cache-Control: no-store`; errors are `{ "error": "..." }` (with `errors`, per
key, when settings are refused).

| Method | Endpoint | Description |
|---|---|---|
| GET | `/` | `{ "marketplaceUrl": "https://plugins.qodalis.com", "install": "verified", "dashboardApi": 1 }` |
| GET | `/plugins?query=&page=&pageSize=` | The marketplace's list fetched with `kind=dashboard&dashboardApi=1` (`{ items, total, page, pageSize }`), plugins of another kind dropped (then `total` counts the items shown), each item annotated (below); 400 `invalid page` (1..1000000), `invalid pageSize` (1..100); 502 `{ "error": "marketplace unreachable" }` |
| GET | `/plugins/{name}` | One plugin with its `versions`, annotated; each version also `allowed` and `blockedReason`; 404 when not listed |
| GET | `/installed` | The installed plugins (below); `updateAvailable` when the marketplace answers |
| GET | `/installed/{name}/config` | `{ values, version }`: the values of its settings (stored over defaults) and the section's version; `{ values: {}, version: null }` without settings |
| POST | `/installed` | `{ name, version?, config? }`: 201 with the installed plugin (`warnings` when its settings were ignored); 400 invalid name or version, `needs dashboard API 2`, `not a dashboard plugin: <name>`, `validation failed` with `errors`, `config must be an object`; 403 `publisher not verified`, `install disabled`; 404 unknown plugin or version; 409 already installed, id taken; 502 `marketplace unreachable`, `integrity mismatch: <path>`, `download failed: <path>`, `file too large: <path>` |
| PUT | `/installed/{name}` | `{ version?, config? }`: updates (default: the newest fitting version), same checks; the plugin with `configDropped`; 404 when not installed |
| DELETE | `/installed/{name}` | Removes the plugin and its settings; 404 when not installed |

Scoped names go in the path raw (`/installed/@acme/tickets`) or encoded (`/installed/%40acme%2Ftickets`).

A `GET /plugins` item is the marketplace's item plus:

```json
{
  "name": "@qodalis/cli-dashboard-module-jobs",
  "kind": "dashboard",
  "publisher": { "name": "@qodalis", "verified": true },
  "version": "1.1.0",
  "installed": { "version": "1.0.0" },
  "updateAvailable": true,
  "allowed": true,
  "requires": { "serverPackage": "Qodalis.Cli.Plugin.Jobs", "installed": true }
}
```

`installed` is null when not installed; `blockedReason` (only when `allowed` is false) is `install disabled`,
`unverified publisher` or `needs dashboard API <n>` (a version of the details may also be `version flagged` or
`not a dashboard plugin`); `requires` is null when the plugin names no .NET server package (`serverPackages.dotnet`, or
the `nuget` entry of the marketplace's list form `[{ ecosystem, name }]`), else whether the server has it
(`GET /plugins/installed`).

`GET /installed`:

```json
[
  {
    "name": "@qodalis/cli-dashboard-module-jobs",
    "version": "1.1.0",
    "title": "Jobs",
    "icon": "⏱️",
    "id": "jobs",
    "installedAt": "2026-11-25T10:04:05.123Z",
    "installedBy": "admin",
    "hasConfig": false,
    "updateAvailable": false
  }
]
```

`hasConfig`: the plugin has settings (the section `dashboard-plugins.<id>`).

## Server plugins from the marketplace

The admin plugin also installs the marketplace's server plugins (`kind=server`), the .NET package of each, using the
same options as dashboard plugins:

```csharp
cli.AddAdmin(o => o.Marketplace = new DashboardMarketplaceOptions
{
    Install = "verified",                                       // or "listed", "off"
    NuGetFeed = "https://api.nuget.org/v3-flatcontainer",       // the default
    TrustedDependencies = ["Npgsql", "Npgsql.*"],
});
```

An install runs these steps, and nothing is stored when a check fails:

1. **Checks.** The trust policy, the plugin's `.NET` platform, `autoInstall`, and the server range of the version
   record (NuGet `[1.0.0-beta.29, )` or npm `^1.0.0-beta.29` syntax, compared with the running `Qodalis.Cli`).
   A package the host already has is refused with 409.
2. **Download.** The `.nupkg` comes from the NuGet flat container (50 MB at most) and must match the version
   record's `integrity` (`sha512-…`). A record without one is refused.
3. **Package.** The nuspec's id and version must match. The `lib/<tfm>` folder is the best one for this runtime
   (`net10.0`, else the nearest lower `netX.Y`, `netcoreapp`, `netstandard2.1`/`2.0`). The main assembly must
   declare `[assembly: CliPlugin(...)]` and must not say `QodalisCliAutoInstall=false`; both are read with
   `System.Reflection.Metadata`, without loading it. A `Qodalis.Cli` dependency's range must include the server's version.
4. **Dependencies.** The server's own packages (`Qodalis.Cli*`, the framework, anything the host references) are
   never downloaded. Every other dependency is installed only in one of two cases; otherwise the install is refused
   with 400 `untrusted dependency: <id>`:
   - The version record pins it with a hash: `dependencies: [{ id, version, integrity }]`. The version must fit the
     nuspec range and the sha512 is checked.
   - Its id matches `TrustedDependencies` (ids, or `Prefix.*`). It is then downloaded from the same feed at the lowest
     version of its range, without a hash.
5. **Extraction.** The `lib` files of the plugin and its dependencies go into a staging folder, then move to
   `<data dir>/server-plugins/<package>/<version>/`. An assembly the host has is never extracted.
6. **Record.** `installed.json` is written atomically and a restart is marked as required (`plugin:<name>`). The
   plugin loads at the next start (see [Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard)).

The admin plugin itself has no entry: it is added in code (`cli.AddAdmin(...)`).

An update installs the new version and deletes the old folder unless this process runs it. A removal deletes the
record (and the files unless they are running), and the plugin keeps running until a restart.

Restart handling, permission, audit and events:

- A restart is marked while the installed list differs from what runs, and cleared when it no longer does.
- Changes run one at a time and need **`plugins:install`** (core catalog, group Server, `CliRoutePolicy.PluginsInstall`).
- They are audited as `server.plugin.install|update|remove` (`details` `<package>@<version>`) and published as
  `server:plugins-changed` (audience `admin:access`).

### Server marketplace API

Routes under `/api/v1/qcli/server/marketplace` need `admin:access` to read and `plugins:install` to change; they
answer 404 when the marketplace is off.

| Route | Does |
|---|---|
| `GET /` | `{ marketplaceUrl, install, platform: "dotnet", serverVersion, enabled }` |
| `GET /plugins?query=&page=&pageSize=&platform=` | `platform`: `this` (default: fetched with `kind=server&platform=dotnet&serverVersion=<this server's>`), `all` (`kind=server`, every server plugin), `node`, `python` or `dotnet` (passed on as `platform=`), 400 for anything else; a plugin without a .NET package is listed with `platform: null`, `allowed: false`, `blockedReason: "not available for dotnet"`. Each item annotated with `platform: { package, latest, installed, compatible, reason? }`, `otherPlatforms`, `autoInstall`, `updateAvailable`, `allowed`, `blockedReason` |
| `GET /plugins/{name}` | One plugin with its platforms and versions (this platform's with `allowed` and `blockedReason`) |
| `GET /installed` | The installed plugins and how they run |
| `POST /installed` `{ name, version? }` | 202: the installed plugin with `restartRequired` |
| `PUT /installed/{name}` `{ version? }` | 202 with `changed` and `previousVersion`; 200 `changed: false` when it already is that version |
| `DELETE /installed/{name}` | 202: the removed plugin |

An installed plugin as `GET /installed` lists it:

```json
{
  "name": "data-explorer-postgres", "package": "Qodalis.Cli.Plugin.DataExplorer.Postgres", "version": "1.0.0-beta.32",
  "title": "Data explorer: PostgreSQL", "icon": "🐘", "description": "...",
  "installedAt": "2026-11-26T10:00:00.000Z", "installedBy": "admin",
  "status": "needs-configuration", "loadedVersion": "1.0.0-beta.32",
  "missing": ["settings.connectionString"], "settings": "data-explorer-postgres",
  "updateAvailable": false
}
```

`status` is one of:

- `loaded`
- `needs-configuration`
- `pending`: installed or updated, runs after a restart
- `failed`, with `error`
- `skipped`: the host has the package
- `removed`: still running until a restart

`settings` is the config provider of its settings: `config set <settings>.settings.<key> <value>`.
Plugins added in code (`cli.AddPlugin`) are not listed here.

## Without CLI routes

On a service built with `cli.WithoutCliRoutes()` the dashboard's API does not exist, so `UseQodalisAdmin()` serves
nothing and logs `[qcli] the CLI routes are turned off (WithoutCliRoutes); the admin dashboard is not served`.

## Terminal commands

The plugin brings the `admin` command (registered by `cli.AddAdmin()`). It reads the same services as the admin
routes. On a protected server every sub-command needs `admin:access`; `admin logs` also needs `logs:read`,
`admin dashboard install|update|remove` also `dashboard:install`, `admin plugins install|update|remove` also
`plugins:install`, `admin restart` (but not `admin restart status`)
`server:restart`. Server settings are the core [`config`](../../README.md#config-providers) command; a dashboard plugin's settings are changed after its install with
`config set dashboard-plugins.<id>.<key> <value>` (`config:write`). Lists take `--json` (the payload of the matching
route).

| Command | Permission | Does |
|---|---|---|
| `admin`, `admin status [--json]` | `admin:access` | Server package and version, platform, uptime, memory, CPU, connections, commands, jobs, features (`GET /status`) |
| `admin status --history [--minutes 60] [--json]` | `admin:access` | Summary of the status samples of the last minutes (1-1440): count, min/avg/max memory and CPU, peak connections (`GET /status/history`) |
| `admin plugins`, `admin plugins list [--json]` | `admin:access` | Command modules: id, name, version, enabled or disabled, commands (`GET /plugins`) |
| `admin plugins enable <id>`, `admin plugins disable <id>` | `admin:access` | Turns a module's commands on or off, like `POST /plugins/{id}/toggle` but idempotent (`... is already enabled.`); the `admin` module itself cannot be disabled from the terminal |
| `admin plugins search [<query>] [--platform this\|all\|node\|python\|dotnet] [--json]` | `admin:access` | The marketplace's server plugins: package, version, platforms, publisher, installable or blocked and why (`GET /server/marketplace/plugins`) |
| `admin plugins installed [--json]` | `admin:access` | The server plugins installed from the marketplace: package, version (update available), status (loaded, needs-configuration, pending restart, failed with the reason), when and by whom |
| `admin plugins install <name>[@version]` | `admin:access`, `plugins:install` | Installs this server's package (the newest compatible version, or the one given); runs after a restart (`POST /server/marketplace/installed`) |
| `admin plugins update <name>[@version]` | `admin:access`, `plugins:install` | Updates an installed server plugin; runs after a restart |
| `admin plugins remove <name>` | `admin:access`, `plugins:install` | Removes it; it runs until a restart |
| `admin clients [--json]` | `admin:access` | Connected WebSocket clients: id, type, connected since, address (`GET /ws/clients`) |
| `admin logs [--level warn,error] [--search <text>] [--service <name>] [--since 5m\|1h\|7d\|<ISO date>] [-n 50] [--json]` | `admin:access`, `logs:read` | The newest matching log entries (1-1000), oldest first, from the log store `GET /logs` reads: the in-memory history, or Seq with `Qodalis.Cli.Plugin.Logs.Seq` (`--service` filters by the service that wrote the entries, which only a persistent store such as Seq records) |
| `admin restart [--yes] [--reason <text>]` | `admin:access`, `server:restart` | Without `--yes` (server commands cannot ask): the changes waiting for a restart and how to confirm; with it, restarts the server like `POST /server/restart` (audited with `via: command`; see [Restart](../../README.md#restart)) |
| `admin restart status [--json]` | `admin:access` | Start time, restart mode and supervisor (or why it cannot restart), the pending changes (`GET /server/restart`) |
| `admin dashboard`, `admin dashboard list [--json]` | `admin:access` | The dashboard plugins installed from the marketplace: page id, package, version (and whether an update is available), when and by whom (`GET /dashboard/marketplace/installed`) |
| `admin dashboard search [<query>] [--json]` | `admin:access` | The marketplace's dashboard plugins: package, version, publisher, installed / installable / blocked and why, a missing server plugin (`GET /dashboard/marketplace/plugins`) |
| `admin dashboard info <name> [--json]` | `admin:access` | An installed plugin: page, files, and its settings with their values, defaults and rules (`*` marks a changed value) |
| `admin dashboard install <name>[@version] [--config '<json>']` | `admin:access`, `dashboard:install` | Installs the newest version this dashboard runs, or the one given, with values for its settings, with the API's checks (`POST /dashboard/marketplace/installed`) |
| `admin dashboard update <name> [--config '<json>']` | `admin:access`, `dashboard:install` | Updates to the newest version, reporting the settings it drops (`PUT /dashboard/marketplace/installed/{name}`) |
| `admin dashboard remove <name>` | `admin:access`, `dashboard:install` | Removes the page and its files (`DELETE /dashboard/marketplace/installed/{name}`) |

`admin dashboard` and `admin plugins search|installed|install|update|remove` are left out when the marketplace is
turned off (`config.Marketplace.Enabled = false`).

```
> admin plugins install weather
Installed weather: Qodalis.Cli.Plugin.Weather@1.0.0-beta.32.
Restart the server to apply it: admin restart (or the dashboard).
```

On a server without the users plugin, `/execute` is open, and so are these commands. To follow the log live, use
`server logs` of the web CLI plugin `@qodalis/cli-server-logs` (the `/ws/v1/qcli/logs` socket, `logs:read`).

```text
$ admin status
Server      Qodalis.Cli 1.0.0-beta.31
Platform    dotnet .NET 10.0.14 (Ubuntu 24.04.1 LTS)
Uptime      2 h 5 min (since 2026-11-25 08:00:00)
Memory      153.19 MB (process) · machine 9.8 GB of 16 GB used (61%)
CPU         1.3%
Connections 2 WebSocket, 0 shell
Commands    15
Jobs        1
Features    filesystem, jobs, audit

$ admin status --history --minutes 15
Last 15 min: 60 sample(s) every 15 s, 2026-11-25 09:50:00 to 2026-11-25 10:04:45
Memory      min 140.1 MB  avg 142.4 MB  max 145.3 MB
CPU         min 0.2%  avg 1.1%  max 5.4%
Peak        3 WebSocket, 1 shell

$ admin plugins
ID          Name        Version        Status   Commands
weather     weather     1.0.0          enabled  1
aws         aws         1.0.0          enabled  1
admin       admin       1.0.0-beta.31  enabled  1

$ admin plugins disable weather
Disabled weather (1 command).

$ admin logs --level warn,error -n 2
Last 2 of 7 entries
2026-11-25 10:01:12 WARN  [Qodalis.Cli.Plugin.Jobs.CliJobScheduler] cleanup took 12 s
2026-11-25 10:03:40 ERROR [Microsoft.EntityFrameworkCore.Database.Command] connection timeout

$ admin dashboard search
Package                             Version  Publisher            Status
@qodalis/cli-dashboard-module-jobs  1.1.0    @qodalis (verified)  installable
@acme/cli-dashboard-tickets         2.0.0    @acme                blocked: unverified publisher

$ admin dashboard install @qodalis/cli-dashboard-module-jobs@1.0.0
Installed @qodalis/cli-dashboard-module-jobs@1.0.0: the dashboard page "Jobs" (/x/jobs).

$ admin dashboard
1 dashboard plugin(s) from https://plugins.qodalis.com (install: verified)
ID    Package                             Version                   Installed            By
jobs  @qodalis/cli-dashboard-module-jobs  1.0.0 (update available)  2026-11-25 10:04:05  admin

$ admin dashboard update @qodalis/cli-dashboard-module-jobs
Updated @qodalis/cli-dashboard-module-jobs from 1.0.0 to 1.1.0.

$ admin restart
1 change(s) take effect after a restart:
  - CORS: Mode changed (config, since 2026-11-25 10:04:05)
Restart mode: self (starts itself again)
Run admin restart --yes [--reason "<why>"] to restart the server now.

$ admin restart --yes --reason "apply CORS"
Restarting the server to apply 1 pending change(s) (self mode). The connection drops and comes back once it runs again.

$ admin restart status
Running since 2026-11-25 10:06:12
Restart mode: exit (exits for docker to start it again)
No changes are waiting for a restart.

$ admin dashboard install @acme/cli-dashboard-tickets --config '{"refreshSeconds":1,"mode":"huge"}'
Error: validation failed
  refreshSeconds: must be at least 5
  mode: must be one of: compact, full

$ admin dashboard install @acme/cli-dashboard-tickets --config '{"refreshSeconds":60}'
Installed @acme/cli-dashboard-tickets@1.0.0: the dashboard page "Tickets" (/x/tickets).
Its settings: admin dashboard info @acme/cli-dashboard-tickets

$ admin dashboard info @acme/cli-dashboard-tickets
@acme/cli-dashboard-tickets@1.0.0
  Page        Tickets (/x/tickets)
  Installed   2026-11-25 10:04:05 by admin
  Files       extension.js (48211 bytes), extension.css (5120 bytes)
Settings (config set dashboard-plugins.tickets.<key> <value>; version 1):
Key             Value              Type      Default            Rule
refreshSeconds  60 *               number    30                 min 5; max 3600
columns         ["name","status"]  string[]  ["name","status"]  -
mode            compact            enum      compact            one of compact, full
* changed from the default

$ config set dashboard-plugins.tickets.mode full
Updated dashboard-plugins.tickets.mode = full (version 2)

$ admin dashboard update @acme/cli-dashboard-tickets --config '{"theme":"dark"}'
Updated @acme/cli-dashboard-tickets from 1.0.0 to 2.0.0.
Settings dropped (the new version no longer accepts them): refreshSeconds, mode
Its settings: admin dashboard info @acme/cli-dashboard-tickets

$ admin dashboard remove @qodalis/cli-dashboard-module-jobs
Removed @qodalis/cli-dashboard-module-jobs@1.1.0 (the page jobs).

$ admin dashboard install @other/cli-dashboard-crm
Error: publisher not verified
```

## License

MIT
