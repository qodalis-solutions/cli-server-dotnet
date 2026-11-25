using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Appearance;
using Qodalis.Cli.Portal;

namespace Qodalis.Cli.Plugin.Portal;

/// <summary>The rules of the portal's options, shared by <c>UseQodalisPortal</c> and the <c>portal</c> config provider.</summary>
internal static partial class PortalValidation
{
    public const int MaxNameLength = 80;
    public const int MaxLogoLength = 200_000;
    public const int MaxFooterLinks = 20;

    public const string NameError = "portal name must be 1-80 characters without control characters";
    public const string LogoError = "portal logo must be an http(s) URL, a root-relative path or a data:image URL";
    public const string FooterLinksShapeError = "must be a list of { \"label\": ..., \"url\": ... } links";

    [GeneratedRegex(@"^data:image/(?:png|jpeg|gif|webp|svg\+xml|x-icon|vnd\.microsoft\.icon);base64,[A-Za-z0-9+/]+={0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DataImage();

    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && !name.Any(char.IsControl);

    public static bool IsValidLogo(string logo) =>
        logo.Length <= MaxLogoLength && (IsWebOrRootRelative(logo) || DataImage().IsMatch(logo));

    /// <summary>The error of a footer link (<c>invalid portal footer link: &lt;label&gt;</c>), or null.</summary>
    public static string? FooterLinkError(PortalLink? link)
    {
        var label = link?.Label ?? string.Empty;
        if (link == null || label.Length is < 1 or > MaxNameLength || label.Any(char.IsControl) ||
            link.Url == null || !IsWebOrRootRelative(link.Url))
            return $"invalid portal footer link: {label}";
        return null;
    }

    /// <summary>
    /// The rule of a new <c>footerLinks</c> value: a list of at most 20 <c>{ label, url }</c> links, each checked as
    /// <see cref="PortalOptions.FooterLinks"/> are. The message, or null.
    /// </summary>
    public static string? FooterLinksError(JsonNode? value)
    {
        if (value is not JsonArray array) return FooterLinksShapeError;
        if (array.Count > MaxFooterLinks) return $"must have at most {MaxFooterLinks} links";
        if (array.Any(item => item is not JsonObject)) return FooterLinksShapeError;
        foreach (var link in ParseLinks(array))
        {
            if (FooterLinkError(link) is { } error) return error;
        }
        return null;
    }

    /// <summary>The links of a JSON list (anything not a string reads as empty / missing).</summary>
    public static List<PortalLink> ParseLinks(JsonArray array) => array
        .OfType<JsonObject>()
        .Select(o => new PortalLink(Text(o["label"]) ?? string.Empty, Text(o["url"])!))
        .ToList();

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>An absolute http(s) URL, or a root-relative path (not <c>//host</c>), without whitespace.</summary>
    public static bool IsWebOrRootRelative(string value)
    {
        if (value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return false;
        if (value[0] == '/') return value.Length == 1 || (value[1] != '/' && value[1] != '\\');
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}

/// <summary>The portal's branding that <c>config set portal.branding.&lt;key&gt;</c> changes (the theme colours stay in code).</summary>
/// <param name="Name">The site name.</param>
/// <param name="Logo">The logo; null: none.</param>
/// <param name="ThemeMode"><c>system</c>, <c>light</c> or <c>dark</c>.</param>
/// <param name="FooterLinks">The footer links.</param>
public sealed record PortalBranding(string Name, string? Logo, string ThemeMode, IReadOnlyList<PortalLink> FooterLinks);

/// <summary>
/// The portal at runtime: the options' branding as the defaults, the current branding (changed live by the
/// <c>portal</c> config provider), where the site was found and the sections. <c>/portal.json</c> and the
/// <c>portal</c> command read it.
/// </summary>
public sealed class PortalState
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] ThemeModes = ["system", "light", "dark"];

    private readonly PortalOptions _options;
    private readonly Lazy<PortalSite?> _site;
    private readonly IReadOnlyList<CliPortalSection> _sections;
    private readonly object _lock = new();
    private PortalBranding _branding;

    public PortalState(PortalOptions options, IServiceProvider services)
    {
        _options = options;
        _sections = services.GetServices<CliPortalSection>().ToList();
        var contentRoot = services.GetService<IHostEnvironment>()?.ContentRootPath ?? Directory.GetCurrentDirectory();
        _site = new Lazy<PortalSite?>(() => PortalSiteResolver.Resolve(options.SitePath, contentRoot));
        Defaults = new PortalBranding(options.Name, options.LogoUrl, ModeName(options.Theme?.Mode ?? PortalThemeMode.System),
            options.FooterLinks.ToList());
        _branding = Defaults;
        ConfigProvider = CreateConfigProvider().SetStore(options.ConfigStore);
    }

    /// <summary>The options' branding: what a reset brings back.</summary>
    public PortalBranding Defaults { get; }

    /// <summary>The branding <c>/portal.json</c> shows now.</summary>
    public PortalBranding Branding
    {
        get
        {
            lock (_lock) return _branding;
        }
    }

    /// <summary>Where the portal is mounted.</summary>
    public string Path => _options.Path;

    /// <summary>The <c>portal</c> config provider.</summary>
    public ICliConfigProvider ConfigProvider { get; }

    internal PortalSite? Site => _site.Value;

    internal IReadOnlyList<CliPortalSection> Sections => _sections;

    private static string ModeName(PortalThemeMode mode) => mode.ToString().ToLowerInvariant();

    private void Update(Func<PortalBranding, PortalBranding> change)
    {
        lock (_lock) _branding = change(_branding);
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static JsonArray LinksJson(IEnumerable<PortalLink> links) =>
        new(links.Select(l => (JsonNode)new JsonObject { ["label"] = l.Label, ["url"] = l.Url }).ToArray());

    /// <summary>
    /// <c>portal</c>: the branding (<c>branding</c>: name, logo, theme mode, footer links), changeable at runtime; the
    /// options' values are the defaults and every change is in the next <c>/portal.json</c>. Where the portal is mounted,
    /// whether its site was found and its sections are read-only (<c>site</c>).
    /// </summary>
    private CliStoredConfigProvider CreateConfigProvider() => new(new CliStoredConfigProviderOptions
    {
        Id = "portal",
        Title = "Portal",
        Description = "Branding of the documentation portal (/portal.json)",
        Order = 80,
        Sections =
        [
            new()
            {
                Id = "branding",
                Title = "Branding",
                Description = "Shown by the portal site; a change is in the next /portal.json, no restart. The theme colours are set in code (Theme).",
                Settings =
                [
                    new()
                    {
                        Key = "name", Label = "Name", Description = "The site name, in the header and the page title",
                        Type = CliConfigEntryTypes.String, Default = Defaults.Name,
                        Validation = new CliConfigValidation { Min = 1, Max = PortalValidation.MaxNameLength },
                        Validate = v => PortalValidation.IsValidName(AsString(v)) ? null : PortalValidation.NameError,
                        Apply = v => Update(b => b with { Name = AsString(v) ?? Defaults.Name }),
                    },
                    new()
                    {
                        Key = "logoUrl", Label = "Logo",
                        Description = "An http(s) URL, a root-relative path or a base64 data:image URL; '' for none",
                        Type = CliConfigEntryTypes.String, Default = Defaults.Logo,
                        Validation = new CliConfigValidation { Max = PortalValidation.MaxLogoLength },
                        Validate = v => AsString(v) is { } s && (s.Length == 0 || PortalValidation.IsValidLogo(s)) ? null : PortalValidation.LogoError,
                        Apply = v => Update(b => b with { Logo = AsString(v) is { Length: > 0 } s ? s : null }),
                    },
                    new()
                    {
                        Key = "themeMode", Label = "Theme", Description = "The colour scheme the site opens in",
                        Type = CliConfigEntryTypes.Enum, Default = Defaults.ThemeMode,
                        Validation = new CliConfigValidation { Options = [.. ThemeModes] },
                        Apply = v => Update(b => b with { ThemeMode = AsString(v) is { } m && ThemeModes.Contains(m) ? m : Defaults.ThemeMode }),
                    },
                    new()
                    {
                        Key = "footerLinks", Label = "Footer links",
                        Description = $"At most {PortalValidation.MaxFooterLinks} links: [{{ \"label\": \"Terms\", \"url\": \"https://example.com/terms\" }}], url http(s) or root-relative",
                        Type = CliConfigEntryTypes.Json, Default = LinksJson(Defaults.FooterLinks),
                        Validate = PortalValidation.FooterLinksError,
                        Apply = v => Update(b => b with
                        {
                            FooterLinks = v is JsonArray a && PortalValidation.FooterLinksError(a) == null ? PortalValidation.ParseLinks(a) : Defaults.FooterLinks,
                        }),
                    },
                ],
            },
            new()
            {
                Id = "site",
                Title = "Site",
                Description = "Set in code (Path, SitePath, AddDocs, AddPublicApi)",
                Settings =
                [
                    new()
                    {
                        Key = "path", Label = "Path", Description = "Where the portal is mounted", Type = CliConfigEntryTypes.String,
                        Mutable = false, RequiresRestart = true, Value = () => _options.Path,
                    },
                    new()
                    {
                        Key = "siteFound", Label = "Site found", Description = "Whether the portal site was found (without it nothing is served)",
                        Type = CliConfigEntryTypes.Boolean, Mutable = false, RequiresRestart = true, Value = () => Site != null,
                    },
                    new()
                    {
                        Key = "sections", Label = "Sections", Description = "The sections published: docs (guides), api (API reference)",
                        Type = CliConfigEntryTypes.StringList, Mutable = false, RequiresRestart = true,
                        Value = () => new JsonArray(_sections.Select(s => (JsonNode)JsonValue.Create(s.Kind)!).ToArray()),
                    },
                ],
            },
        ],
    });

    /// <summary>
    /// <c>portal.json</c> for a request's path base, with the current branding: <c>name</c>, <c>logo</c>, <c>theme</c>,
    /// <c>footerLinks</c>, <c>docs</c>, <c>api</c>. With <paramref name="status"/>: <c>docs.pageCount</c> and <c>site</c>
    /// too (<c>portal --json</c>).
    /// </summary>
    internal JsonObject Document(string pathBase, bool status = false)
    {
        var branding = Branding;
        var theme = _options.Theme ?? new PortalTheme();
        var json = new JsonObject
        {
            ["name"] = branding.Name,
            ["logo"] = branding.Logo,
            ["theme"] = new JsonObject
            {
                ["mode"] = branding.ThemeMode,
                ["dark"] = Colours(theme.Dark),
                ["light"] = Colours(theme.Light),
            },
            ["footerLinks"] = LinksJson(branding.FooterLinks),
        };
        var docs = _sections.FirstOrDefault(s => s.Kind == CliPortalSection.Docs);
        var api = _sections.FirstOrDefault(s => s.Kind == CliPortalSection.Api);
        if (docs == null) json["docs"] = null;
        else
        {
            var docsJson = new JsonObject { ["title"] = docs.Title, ["pages"] = pathBase + docs.BasePath + "/pages" };
            if (status) docsJson["pageCount"] = PageCount(docs);
            json["docs"] = docsJson;
        }
        json["api"] = api == null
            ? null
            : new JsonObject
            {
                ["title"] = api.Title,
                ["openapi"] = pathBase + api.BasePath + "/openapi.json",
                ["swagger"] = pathBase + api.BasePath + "/swagger",
            };
        if (status)
        {
            var site = Site;
            json["site"] = new JsonObject
            {
                ["path"] = _options.Path,
                ["found"] = site != null,
                ["source"] = site?.Source,
                ["dir"] = site?.Directory,
            };
        }
        return json;
    }

    private static int? PageCount(CliPortalSection docs)
    {
        try
        {
            return docs.PageCount?.Invoke();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static JsonObject Colours(PortalThemeColors? colours)
    {
        var json = new JsonObject();
        if (colours == null) return json;
        foreach (var (token, value) in colours.Entries())
        {
            if (value != null && CliColor.IsValid(value)) json[token] = value.Trim();
        }
        return json;
    }

    /// <summary>Validates the options as <c>UseQodalisPortal</c> does: <see cref="ArgumentException"/> on the first error.</summary>
    internal void Validate()
    {
        if (!PortalValidation.IsValidName(_options.Name)) throw new ArgumentException(PortalValidation.NameError);
        if (_options.LogoUrl is { } logo && !PortalValidation.IsValidLogo(logo)) throw new ArgumentException(PortalValidation.LogoError);
        var theme = _options.Theme ?? new PortalTheme();
        if (!Enum.IsDefined(theme.Mode)) throw new ArgumentException($"invalid portal theme mode: {theme.Mode}");
        CheckColours(theme.Dark, "dark");
        CheckColours(theme.Light, "light");
        if (_options.FooterLinks.Count > PortalValidation.MaxFooterLinks)
            throw new ArgumentException($"invalid portal footer link: {_options.FooterLinks[PortalValidation.MaxFooterLinks].Label}");
        foreach (var link in _options.FooterLinks)
        {
            if (PortalValidation.FooterLinkError(link) is { } error) throw new ArgumentException(error);
        }
    }

    private static void CheckColours(PortalThemeColors? colours, string scheme)
    {
        if (colours == null) return;
        foreach (var (token, value) in colours.Entries())
        {
            if (value != null && !CliColor.IsValid(value)) throw new ArgumentException($"invalid portal colour {scheme}.{token}: {value}");
        }
    }
}
