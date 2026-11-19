# Qodalis.Cli.Plugin.Docs

Docs plugin for the Qodalis CLI Server: markdown guides from folders or code, with front matter and variables, served
as JSON. The portal plugin shows them as the portal site's guides; any other client can read them too.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.Docs
```

## Quick start

```csharp
using Qodalis.Cli.Plugin.Docs;

builder.Services.AddControllers().AddCli(cli => cli.AddDocs(o =>
{
    o.Name = "RateBell API";
    o.BaseUrl = "https://api.ratebell.app";
    o.Variables["support"] = "help@ratebell.app";
    o.AddFolder("docs");                               // relative to the content root
    o.AddPage("errors", "# Errors\nEvery error is `{ \"error\": \"…\" }`.");
}));

var app = builder.Build();
app.UseRouting();
app.UseQodalisDocs(); // reads the folders now
app.MapControllers();
```

## Options

| Option | Default | Description |
|---|---|---|
| `BasePath` | `/qcli/docs` | Where the pages are served; starts with `/`, no trailing `/`, not `/` (else `invalid docs base path: <path>`) |
| `Title` | `Guides` | The guides section's label on the portal |
| `Name` | `Documentation` | The built-in variable `{{ name }}` |
| `BaseUrl` | the request's `scheme://host[pathBase]` | The built-in variable `{{ baseUrl }}` (no trailing slash). **Set it in production**: otherwise the request's `Host` header ends up in the pages |
| `Variables` | empty | The host's variables, e.g. `o.Variables["support"] = "help@ratebell.app"` |
| `AddFolder(path)` | | Every `*.md` file directly in the folder |
| `AddPage(slug, markdown)` | | A page from code |

## Routes

| Route | Answer |
|---|---|
| `GET <BasePath>/pages` | 200, `Cache-Control: no-cache`: `[ { "slug", "title", "group", "order" } ]` in page order |
| `GET <BasePath>/pages/<slug>` | 200, `Cache-Control: no-cache`: `{ "slug", "title", "group", "order", "markdown" }` |
| unknown or invalid slug | 404 `{ "error": "page not found" }` |

`HEAD` too. No sign-in; credentials are ignored. Page order: `order`, then `title` (case-insensitive), then `slug`.

## Pages

- **Folders**: every `*.md` file directly in the folder (no subfolders; names starting with `.` or `_` are ignored).
  The slug is the file name without `.md`, lower-cased, and must match `^[a-z0-9][a-z0-9-]*$`. `index` is the
  guides' home page. Folders are read once, when `UseQodalisDocs()` runs.
- **Front matter**: only when the first line is exactly `---`, up to the next line that is exactly `---` (without one,
  the whole file is the body). Each line is `key: value`; the keys are `title`, `group` and `order` (others are
  ignored), a value in matching `"…"` or `'…'` is unquoted, and `order` is an integer. The front matter is removed
  from `markdown`.
- **Defaults**: `title` = the first body line starting with `# `, else the slug; `group` = `Guides`; `order` = `0`.
- **Variables**: `{{ key }}` in titles and markdown, replaced per request; unknown keys are left as written.

```markdown
---
title: Authentication
group: Basics
order: 2
---
Send `Authorization: Bearer <token>` to {{ baseUrl }}/v1/rates. Questions: {{ support }}.
```

## Startup errors

`ArgumentException` with one of: `invalid docs page slug: <slug>`, `invalid docs page name: <file name>`,
`duplicate docs page: <slug>`, `docs folder not found: <path>`, `invalid order in docs page: <slug>`,
`invalid docs variable: <key>`, `docs variable is built in: <key>` (`baseUrl`, `name`), `invalid docs base path: <path>`.

## License

MIT
