# Qodalis.Cli.Plugin.Docs

Docs plugin for the Qodalis CLI Server: markdown guides from folders or code, with front matter and variables, served
as JSON for the portal site.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.Docs
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.Docs;

builder.Services.AddControllers().AddCli(cli => cli.AddDocs(o => o.AddFolder("docs")));

app.UseRouting();
app.UseQodalisDocs();
```

## License

MIT
