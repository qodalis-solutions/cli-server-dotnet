# Qodalis.Cli.Plugin.Portal

Portal plugin for the Qodalis CLI Server: the prebuilt documentation site (`@qodalis/cli-server-portal`) at one
mount, with the guides of the [docs plugin](../docs/README.md) and the API reference of the
[public-api plugin](../public-api/README.md) (OpenAPI document, code samples, Swagger UI). The plugins never reference
each other: each registers its section and the portal lists the sections it finds.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.Portal
```

## Quick start

```csharp
using Qodalis.Cli.Plugin.Docs;
using Qodalis.Cli.Plugin.Portal;
using Qodalis.Cli.Plugin.PublicApi;

builder.Services.AddOpenApi("public", o => o.ShouldInclude = d => d.GroupName == "public");
builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddUsers();
    cli.AddPublicApi();
    cli.AddDocs(o => o.AddFolder("docs"));
    cli.AddPortal(o =>
    {
        o.Path = "/";                       // RateBell serves its docs at the root
        o.Name = "RateBell API";
        o.LogoUrl = "/logo.svg";
        o.Theme.Mode = PortalThemeMode.System;
        o.Theme.Light.Primary = "#0a7";
        o.FooterLinks.Add(new PortalLink("Terms", "https://ratebell.app/terms"));
    });
});

var app = builder.Build();
app.UseRouting();
app.UseQodalisUsers();
app.UseQodalisDocs();
app.UseQodalisPublicApi();
app.UseQodalisPortal();
app.MapControllers();
```

## Options

| Option | Default | Description |
|---|---|---|
| `Path` | `/qcli/portal` | The mount: starts with `/`, no trailing `/`; `/` mounts the site at the root. A mount holding the CLI's routes or inside them (`/api`, `/ws`, `/api/v1`, `/api/v1/qcli/...`, `/api/qcli/...`, `/api/cli/...`, any case) is refused |
| `Name` | `Documentation` | The portal's name: 1-80 characters without control characters |
| `LogoUrl` | `null` | An http(s) URL, a root-relative path or a base64 `data:image/…` URL (up to 200 000 characters) |
| `Theme` | mode `System`, no colours | `Mode` (`System`, `Dark`, `Light`) and the `Dark` / `Light` colours: the admin dashboard's 21 tokens (`Primary`, `PrimaryHover`, `OnPrimary`, `Accent`, `Background`, `BackgroundAccent`, `Sidebar`, `Surface`, `Popover`, `Control`, `ControlHover`, `Border`, `Overlay`, `Heading`, `Text`, `Muted`, `Subtle`, `Success`, `Warning`, `Error`, `Info`) |
| `FooterLinks` | empty | At most 20 `PortalLink(Label, Url)`: label 1-80 characters, URL http(s) or root-relative |
| `SitePath` | none | A folder with the built site, used instead of the package's (and then only that folder) |

The options are checked when `UseQodalisPortal()` runs (`ArgumentException`): `portal name must be 1-80 characters
without control characters`, `portal logo must be an http(s) URL, a root-relative path or a data:image URL`,
`invalid portal colour <scheme>.<token>: <value>`, `invalid portal theme mode: <mode>` (a `Mode` outside
`System`, `Dark`, `Light`), `invalid portal footer link: <label>`, `invalid portal path: <path>`.
Two sections of one kind fail with `InvalidOperationException("portal section already registered: <kind>")`.

## Routes

The portal answers exactly these paths under its mount, `GET` and `HEAD` only; anything else falls through to the
host, so a portal mounted at `/` never shadows the host's routes (`/v1/rates`, `/health`):

| Path (relative to the mount) | Answer |
|---|---|
| the mount, `/`, `/guides/<slug>`, `/reference`, `/reference/<tag>` | The site's `index.html` (`text/html; charset=utf-8`, `Cache-Control: no-cache`), its `<base href="/" />` pointed at the mount |
| `/portal.json` | What the site shows (below), `Cache-Control: no-cache` |
| `/assets/<file>`, `/favicon.svg` | The site's file when it exists (`Cache-Control: public, max-age=31536000, immutable` for `/assets/`) |

```json
{
  "name": "RateBell API",
  "logo": "/logo.svg",
  "theme": { "mode": "system", "dark": {}, "light": { "primary": "#0a7" } },
  "footerLinks": [ { "label": "Terms", "url": "https://ratebell.app/terms" } ],
  "docs": { "title": "Guides", "pages": "/qcli/docs/pages" },
  "api": { "title": "API reference", "openapi": "/qcli/api/openapi.json", "swagger": "/qcli/api/swagger" }
}
```

`docs` and `api` are `null` when that plugin is not added; with neither the site shows "Nothing published yet".

A request path base (`UsePathBase`, a proxy's prefix) is put in front of the `<base href>` and the `portal.json` URLs.
A path base starting with `//` or `/\`, or holding whitespace or a control character (browsers drop tabs and line
breaks, so `/\t/evil.example` becomes `//evil.example`), would point them at another host and answers 400
`{"error":"invalid path base"}`.

## The site

The package embeds the site at build time, preferring the sibling repo's `cli-server-dashboard/portal/dist`, then the
npm package `@qodalis/cli-server-portal` (`npm install` in this folder). At run time the plugin looks for
`node_modules/@qodalis/cli-server-portal/dist` and the sibling repo near the content root, then the embedded copy.
Without a site it logs `[qcli] portal site not found; the portal is not served` once and serves nothing,
`portal.json` included.

## Terminal commands

`portal`, registered by `AddPortal` (with a `portal` module that describes it). It needs `config:read` on a protected
server: it shows the portal's settings, as `config get portal` does (the portal itself stays public). The site's content
comes from the [docs plugin](../docs/README.md#terminal-commands) (`docs list`, `docs read`) and the
[public-api plugin](../public-api/README.md#terminal-commands) (`api usage`).

| Command | Permission | Does |
|---|---|---|
| `portal`, `portal status [--json]` | `config:read` | The mount, whether the site was found and where from, the sections (docs with its page count, the API's title and OpenAPI URL) and the current branding |

```text
$ portal
Portal: /qcli/portal
Site: found, @qodalis/cli-server-portal (/app/node_modules/@qodalis/cli-server-portal/dist)
Sections:
  docs  Guides, 12 pages (/qcli/docs/pages)
  api   API reference (/qcli/api/openapi.json)
Branding:
  name         RateBell API
  logoUrl      /logo.svg
  themeMode    system
  footerLinks  Terms (https://example.com/terms)

Change the branding with config set portal.branding.<key> <value> (name, logoUrl, themeMode, footerLinks).
```

Where the site came from: `SitePath`, `@qodalis/cli-server-portal`, `cli-server-dashboard/portal/dist` or
`bundled with the plugin` (the embedded copy, shown without a folder); without one,
`Site: not found, the portal is not served (install @qodalis/cli-server-portal or set SitePath)`. Without sections:
`Sections: none (the site shows "Nothing published yet")`.

`--json` prints `portal.json` (with the current branding; `docs` adds `pageCount`) and the site:

```json
{
  "name": "RateBell API",
  "logo": "/logo.svg",
  "theme": { "mode": "system", "dark": {}, "light": {} },
  "footerLinks": [{ "label": "Terms", "url": "https://example.com/terms" }],
  "docs": { "title": "Guides", "pages": "/qcli/docs/pages", "pageCount": 12 },
  "api": { "title": "API reference", "openapi": "/qcli/api/openapi.json", "swagger": "/qcli/api/swagger" },
  "site": { "path": "/qcli/portal", "found": true, "source": "package", "dir": "/app/node_modules/@qodalis/cli-server-portal/dist" }
}
```

`site.source` is `siteDir`, `package`, `checkout`, `bundled`, or `null` when the site was not found; `site.dir` is
`null` for the embedded copy.

## Configuration

The plugin brings the `portal` config provider (the server's [config providers](../../README.md#config-providers)):
the branding changes at runtime, through `config set`, the dashboard's Settings page or
`PATCH /api/v1/qcli/config/portal/branding` (`config:write`). The options' values are the defaults, stored values
override them, and every change is in the next `/portal.json`, no restart. `null` (`config reset`) brings the options'
value back.

| Setting | Type | Rules |
|---|---|---|
| `portal.branding.name` | string | 1-80 characters without control characters |
| `portal.branding.logoUrl` | string | An http(s) URL, a root-relative path or a base64 `data:image/…` URL (up to 200 000 characters), as `LogoUrl` is checked; `''` for no logo. A `string` rather than a `url`: a `url` setting must be absolute |
| `portal.branding.themeMode` | enum | `system`, `light` or `dark` |
| `portal.branding.footerLinks` | json | A list of at most 20 `{ "label", "url" }` links, as `FooterLinks` are checked |
| `portal.site.path`, `.siteFound`, `.sections` | read-only | The mount, whether the site was found, the sections added (`docs`, `api`) |

```text
$ config set portal.branding.name "RateBell API"
$ config set portal.branding.themeMode dark
$ config set portal.branding.footerLinks '[{"label":"Terms","url":"https://example.com/terms"}]'
$ config reset portal.branding.logoUrl
```

The theme's colour tokens (`Theme.Dark`, `Theme.Light`) stay in code: 21 tokens per scheme would make a long settings
page, and a wrong colour is easier to catch in review.

## License

MIT
