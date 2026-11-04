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
| `/api/v1/qcli/auth/login` | POST | Authenticate and receive a JWT token |
| `/api/v1/qcli/auth/me` | GET | Get current authenticated user info |
| `/api/v1/qcli/status` | GET | Server status (uptime, memory, features) |
| `/api/v1/qcli/config` | GET | View server configuration sections |
| `/api/v1/qcli/logs` | GET | Query recent log entries with filtering |
| `/api/v1/qcli/plugins` | GET | List registered plugin modules |
| `/api/v1/qcli/plugins/{name}/toggle` | POST | Enable or disable a plugin module |
| `/api/v1/qcli/ws/clients` | GET | List active WebSocket connections |

## Authentication

Login is rate-limited to 5 attempts per IP per minute. Successful login returns a JWT token that must be included as a `Bearer` token in subsequent requests to protected admin endpoints.

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

## License

MIT
