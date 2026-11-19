# Qodalis.Cli.Plugin.Portal

Portal plugin for the Qodalis CLI Server: the prebuilt documentation site (`@qodalis/cli-server-portal`) at one mount,
showing the docs plugin's guides and the public-api plugin's API reference.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.Portal
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.Portal;

builder.Services.AddControllers().AddCli(cli => cli.AddPortal(o => o.Name = "RateBell API"));

app.UseRouting();
app.UseQodalisPortal();
```

## License

MIT
