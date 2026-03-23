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

## License

MIT
