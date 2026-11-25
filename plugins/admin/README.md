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
| `/api/v1/qcli/status` | GET | Server status (uptime, memory, features) |
| `/api/v1/qcli/status/history` | GET | The status sampled every 15 seconds (memory, CPU % of one core, WebSocket clients, shell sessions), oldest first: `minutes` (default 60, 1..1440). Kept in memory for 24 hours |
| `/api/v1/qcli/config` | GET | `{ providers }`: the server's [config providers](../../README.md#config-providers) the caller may see (`config:read`) |
| `/api/v1/qcli/config/{provider}` | GET | One provider |
| `/api/v1/qcli/config/{provider}/{section}` | PATCH | `{ changes, version? }` (`config:write`): 200 with the section; 400 per-key errors; 409 with the current version |
| `/api/v1/qcli/logs` | GET | Query log entries: `level` (comma list), `search`, `service`, `from`, `to`, `limit`, `offset`. The latest 1000 entries of this process, or Seq with [`Qodalis.Cli.Plugin.Logs.Seq`](../logs-seq/README.md) (any `ILogStore`) |
| `/api/v1/qcli/logs/histogram` | GET | Entries per level (`DEBUG`, `INFO`, `WARN`, `ERROR`) in `buckets` equal columns (default 24, 1..200) of `[from, to)` (default the last 24 hours), with the filters of `/logs`. Counts the buffer, or asks Seq to count |
| `/api/v1/qcli/plugins` | GET | List registered plugin modules |
| `/api/v1/qcli/plugins/installed` | GET | List the installed server plugin packages (`id`, `name`, `version`, `description`) |
| `/api/v1/qcli/plugins/{name}/toggle` | POST | Enable or disable a plugin module |
| `/api/v1/qcli/ws/clients` | GET | List active WebSocket connections |

The plugin registers two config providers: `auth` (read-only: the sign-in mode, the static admin's username, whether
the default credentials or an explicit JWT secret are in use, the token lifetime and the sign-in URL; never a password or
the secret) and `logs` (the ring buffer's size, 100 to 100000 entries, and its lowest level, changeable at runtime).

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

## Without CLI routes

On a service built with `cli.WithoutCliRoutes()` the dashboard's API does not exist, so `UseQodalisAdmin()` serves
nothing and logs `[qcli] the CLI routes are turned off (WithoutCliRoutes); the admin dashboard is not served`.

## Terminal commands

The plugin brings the `admin` command (registered by `cli.AddAdmin()`). It reads the same services as the admin
routes. On a protected server every sub-command needs `admin:access`; `admin logs` also needs `logs:read`. Server
settings are the core [`config`](../../README.md#config-providers) command. Lists take `--json` (the payload of the
matching route).

| Command | Permission | Does |
|---|---|---|
| `admin`, `admin status [--json]` | `admin:access` | Server package and version, platform, uptime, memory, CPU, connections, commands, jobs, features (`GET /status`) |
| `admin status --history [--minutes 60] [--json]` | `admin:access` | Summary of the status samples of the last minutes (1-1440): count, min/avg/max memory and CPU, peak connections (`GET /status/history`) |
| `admin plugins`, `admin plugins list [--json]` | `admin:access` | Command modules: id, name, version, enabled or disabled, commands (`GET /plugins`) |
| `admin plugins enable <id>`, `admin plugins disable <id>` | `admin:access` | Turns a module's commands on or off, like `POST /plugins/{id}/toggle` but idempotent (`... is already enabled.`); the `admin` module itself cannot be disabled from the terminal |
| `admin clients [--json]` | `admin:access` | Connected WebSocket clients: id, type, connected since, address (`GET /ws/clients`) |
| `admin logs [--level warn,error] [--search <text>] [--service <name>] [--since 5m\|1h\|7d\|<ISO date>] [-n 50] [--json]` | `admin:access`, `logs:read` | The newest matching log entries (1-1000), oldest first, from the log store `GET /logs` reads: the in-memory history, or Seq with `Qodalis.Cli.Plugin.Logs.Seq` (`--service` filters by the service that wrote the entries, which only a persistent store such as Seq records) |

On a server without the users plugin, `/execute` is open, and so are these commands. To follow the log live, use
`server logs` of the web CLI plugin `@qodalis/cli-server-logs` (the `/ws/v1/qcli/logs` socket, `logs:read`).

```text
$ admin status
Server      Qodalis.Cli 1.0.0-beta.31
Platform    dotnet .NET 10.0.14 (Ubuntu 24.04.1 LTS)
Uptime      2 h 5 min (since 2026-11-25 08:00:00)
Memory      153.19 MB
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
```

## License

MIT
