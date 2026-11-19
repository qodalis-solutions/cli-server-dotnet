# Qodalis.Cli.Plugin.PublicApi

Public API plugin for the Qodalis CLI Server: guards the host's own routes with `host` API tokens, counts every request
per token and day, enforces per-account daily and per-minute quotas, and shows the usage on the users plugin's token
views.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.PublicApi
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.PublicApi;

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        cli.AddPermissions(p => p.Group("Rates").Add("rates:read", "Official exchange rates"));
        cli.AddUsers();
        cli.AddPublicApi(o => o.UseQuotaResolver<PlanQuotaResolver>());
    });

app.MapGet("/v1/rates", (HttpContext ctx) => rates.Today(ctx.GetQcliCaller()!.UserId))
    .RequirePublicApi("rates:read");
```

## License

MIT
