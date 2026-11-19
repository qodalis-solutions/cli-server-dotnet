using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Appearance;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Portal;

namespace Qodalis.Cli.Plugin.Portal;

/// <summary>Registers and serves the portal plugin.</summary>
public static partial class CliBuilderPortalExtensions
{
    internal const string SiteNotFoundMessage = "[qcli] portal site not found; the portal is not served";
    private const int MaxNameLength = 80;
    private const int MaxLogoLength = 200_000;
    private const int MaxFooterLinks = 20;

    [GeneratedRegex(@"^data:image/(?:png|jpeg|gif|webp|svg\+xml|x-icon|vnd\.microsoft\.icon);base64,[A-Za-z0-9+/]+={0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DataImage();

    [GeneratedRegex("<base\\s+href=\"/\"\\s*/?>")]
    private static partial Regex BaseTag();

    // The CLI's routes: /api/<v>/qcli/..., /api/qcli/..., /api/cli/..., /ws/... (matched with a trailing "/").
    [GeneratedRegex(@"^/(api|ws)/(v\d+/)?(qcli|cli)(/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CliPath();

    // Their parents: /api, /ws, /api/v1, /ws/v1.
    [GeneratedRegex(@"^/(api|ws)(/v\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CliParent();

    /// <summary>A mount that holds the CLI's routes or sits inside them.</summary>
    private static bool HoldsCliRoutes(string path) => CliParent().IsMatch(path) || CliPath().IsMatch(path + "/");

    /// <summary>
    /// Adds the portal plugin: the prebuilt portal site (<c>@qodalis/cli-server-portal</c>) at
    /// <see cref="PortalOptions.Path"/>, showing the guides of the docs plugin and the API reference of the public-api
    /// plugin when those are added. Served by <see cref="UseQodalisPortal"/>.
    /// </summary>
    public static CliBuilder AddPortal(this CliBuilder builder, Action<PortalOptions>? configure = null)
    {
        var options = new PortalOptions();
        configure?.Invoke(options);
        builder.AddInstalledPlugin(typeof(PortalOptions).Assembly);
        builder.Services.AddSingleton(options);
        return builder;
    }

    /// <summary>
    /// Serves the portal: the site's <c>index.html</c> (its <c>&lt;base href="/" /&gt;</c> pointed at the mount) for the
    /// mount, <c>/guides/&lt;slug&gt;</c>, <c>/reference</c> and <c>/reference/&lt;tag&gt;</c>; <c>/portal.json</c>; the
    /// site's <c>/assets/*</c> files and <c>/favicon.svg</c>. GET and HEAD only; every other path falls through, so a
    /// portal mounted at <c>/</c> never shadows the host's routes. Validates the options (<see cref="ArgumentException"/>)
    /// and the sections (<see cref="InvalidOperationException"/> for a second section of one kind). Without a site it
    /// logs a warning once and serves nothing.
    /// </summary>
    public static IApplicationBuilder UseQodalisPortal(this IApplicationBuilder app)
    {
        var services = app.ApplicationServices;
        var options = services.GetService<PortalOptions>()
                      ?? throw new InvalidOperationException("portal: call cli.AddPortal() before UseQodalisPortal()");
        var mount = ValidatePath(options.Path);
        var sections = Sections(services.GetServices<CliPortalSection>());
        var portalJson = PortalJson(options, sections);

        var contentRoot = services.GetService<IHostEnvironment>()?.ContentRootPath ?? Directory.GetCurrentDirectory();
        var site = PortalSiteResolver.Resolve(options.SitePath, contentRoot);
        var index = site == null ? null : ReadIndex(site);
        if (site == null || index == null)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger("Qodalis.Cli.Plugin.Portal").LogWarning(SiteNotFoundMessage);
            return app;
        }

        var prefix = mount == "/" ? string.Empty : mount;
        var rootIndex = RewriteBase(index, prefix + "/");
        var contentTypes = new FileExtensionContentTypeProvider();

        app.Use(async (context, next) =>
        {
            var method = context.Request.Method;
            var path = context.Request.Path.Value ?? string.Empty;
            if (!(HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) || Relative(path, prefix) is not { } relative)
            {
                await next();
                return;
            }

            var response = context.Response;
            var pathBase = context.Request.PathBase.Value ?? string.Empty;
            var isSiteRoute = IsSiteRoute(relative);
            if ((isSiteRoute || relative == "/portal.json") && IsProtocolRelative(pathBase))
            {
                // A path base like //evil.example would turn the site's base and portal.json's URLs into another host.
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ContentType = "application/json; charset=utf-8";
                await response.WriteAsync(InvalidPathBaseBody, context.RequestAborted);
                return;
            }
            if (isSiteRoute)
            {
                await WriteAsync(context, "text/html; charset=utf-8",
                    pathBase.Length == 0 ? rootIndex : RewriteBase(index, pathBase + prefix + "/"));
                return;
            }
            if (relative == "/portal.json")
            {
                await WriteAsync(context, "application/json; charset=utf-8", portalJson(pathBase));
                return;
            }
            if (AssetPath(relative) is { } assetPath && site.GetFileInfo(assetPath) is { Exists: true, IsDirectory: false } file)
            {
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = contentTypes.TryGetContentType(file.Name, out var type) ? type : "application/octet-stream";
                response.ContentLength = file.Length;
                if (assetPath.StartsWith("assets/", StringComparison.Ordinal))
                    response.Headers.CacheControl = "public, max-age=31536000, immutable";
                if (HttpMethods.IsGet(method))
                {
                    await using var stream = file.CreateReadStream();
                    await stream.CopyToAsync(response.Body, context.RequestAborted);
                }
                return;
            }
            await next();
        });
        return app;
    }

    internal const string InvalidPathBaseBody = "{\"error\":\"invalid path base\"}";

    private static bool IsProtocolRelative(string pathBase) =>
        pathBase.Length >= 2 && pathBase[0] == '/' && (pathBase[1] == '/' || pathBase[1] == '\\');

    /// <summary>The path below the mount (<c>""</c> for the mount itself), or null when it is not under it.</summary>
    private static string? Relative(string path, string prefix)
    {
        if (prefix.Length == 0) return path;
        if (path == prefix) return string.Empty;
        return path.StartsWith(prefix + "/", StringComparison.Ordinal) ? path[prefix.Length..] : null;
    }

    /// <summary>The mount, <c>/</c>, <c>/guides/&lt;slug&gt;</c>, <c>/reference</c>, <c>/reference/&lt;tag&gt;</c>.</summary>
    private static bool IsSiteRoute(string relative) =>
        relative is "" or "/" or "/reference" ||
        OneSegment(relative, "/guides/") || OneSegment(relative, "/reference/");

    private static bool OneSegment(string relative, string prefix) =>
        relative.StartsWith(prefix, StringComparison.Ordinal) && relative.Length > prefix.Length &&
        relative.IndexOf('/', prefix.Length) < 0;

    /// <summary><c>assets/&lt;file&gt;</c> (no deeper folders, no dot segments) or <c>favicon.svg</c>.</summary>
    private static string? AssetPath(string relative)
    {
        if (relative == "/favicon.svg") return "favicon.svg";
        if (!OneSegment(relative, "/assets/")) return null;
        var name = relative["/assets/".Length..];
        return name.StartsWith('.') || name.Contains('\\') ? null : "assets/" + name;
    }

    private static async Task WriteAsync(HttpContext context, string contentType, string body)
    {
        var response = context.Response;
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = contentType;
        response.Headers.CacheControl = "no-cache";
        response.ContentLength = bytes.Length;
        if (HttpMethods.IsGet(context.Request.Method))
            await response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private static string? ReadIndex(IFileProvider site)
    {
        var file = site.GetFileInfo("index.html");
        if (!file.Exists) return null;
        using var reader = new StreamReader(file.CreateReadStream(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Points the site's single <c>&lt;base href="/" /&gt;</c> at <paramref name="href"/>.</summary>
    private static string RewriteBase(string index, string href) =>
        BaseTag().Replace(index, $"<base href=\"{System.Net.WebUtility.HtmlEncode(href)}\" />", 1);

    // -------------------------------------------------------------------------
    // portal.json and validation
    // -------------------------------------------------------------------------

    private static string ValidatePath(string? path)
    {
        var valid = path == "/" ||
                    (!string.IsNullOrEmpty(path) && path[0] == '/' && path[^1] != '/' &&
                     !path.Contains("//", StringComparison.Ordinal) &&
                     !path.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '?' or '#' or '\\' or '"' or '<' or '>') &&
                     !HoldsCliRoutes(path));
        if (!valid) throw new ArgumentException($"invalid portal path: {path}");
        return path!;
    }

    private static Dictionary<string, CliPortalSection> Sections(IEnumerable<CliPortalSection> sections)
    {
        var result = new Dictionary<string, CliPortalSection>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            if (!result.TryAdd(section.Kind, section))
                throw new InvalidOperationException($"portal section already registered: {section.Kind}");
        }
        return result;
    }

    /// <summary>Validates the options and returns <c>portal.json</c> for a request's path base.</summary>
    private static Func<string, string> PortalJson(PortalOptions options, IReadOnlyDictionary<string, CliPortalSection> sections)
    {
        var name = options.Name;
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name.Any(char.IsControl))
            throw new ArgumentException("portal name must be 1-80 characters without control characters");

        var logo = options.LogoUrl;
        if (logo != null && !(logo.Length <= MaxLogoLength && (IsWebOrRootRelative(logo) || DataImage().IsMatch(logo))))
            throw new ArgumentException("portal logo must be an http(s) URL, a root-relative path or a data:image URL");

        var theme = options.Theme ?? new PortalTheme();
        if (!Enum.IsDefined(theme.Mode))
            throw new ArgumentException($"invalid portal theme mode: {theme.Mode}");
        var dark = Colours(theme.Dark, "dark");
        var light = Colours(theme.Light, "light");

        if (options.FooterLinks.Count > MaxFooterLinks)
            throw new ArgumentException($"invalid portal footer link: {options.FooterLinks[MaxFooterLinks].Label}");
        foreach (var link in options.FooterLinks)
        {
            var label = link?.Label ?? string.Empty;
            if (link == null || label.Length is < 1 or > MaxNameLength || label.Any(char.IsControl) ||
                link.Url == null || !IsWebOrRootRelative(link.Url))
                throw new ArgumentException($"invalid portal footer link: {label}");
        }

        sections.TryGetValue(CliPortalSection.Docs, out var docs);
        sections.TryGetValue(CliPortalSection.Api, out var api);

        return pathBase =>
        {
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions
                   {
                       Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
            {
                json.WriteStartObject();
                json.WriteString("name", name);
                if (logo == null) json.WriteNull("logo");
                else json.WriteString("logo", logo);

                json.WriteStartObject("theme");
                json.WriteString("mode", theme.Mode.ToString().ToLowerInvariant());
                WriteColours(json, "dark", dark);
                WriteColours(json, "light", light);
                json.WriteEndObject();

                json.WriteStartArray("footerLinks");
                foreach (var link in options.FooterLinks)
                {
                    json.WriteStartObject();
                    json.WriteString("label", link.Label);
                    json.WriteString("url", link.Url);
                    json.WriteEndObject();
                }
                json.WriteEndArray();

                if (docs == null) json.WriteNull("docs");
                else
                {
                    json.WriteStartObject("docs");
                    json.WriteString("title", docs.Title);
                    json.WriteString("pages", pathBase + docs.BasePath + "/pages");
                    json.WriteEndObject();
                }

                if (api == null) json.WriteNull("api");
                else
                {
                    json.WriteStartObject("api");
                    json.WriteString("title", api.Title);
                    json.WriteString("openapi", pathBase + api.BasePath + "/openapi.json");
                    json.WriteString("swagger", pathBase + api.BasePath + "/swagger");
                    json.WriteEndObject();
                }
                json.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        };
    }

    private static List<(string Token, string Value)> Colours(PortalThemeColors? colours, string scheme)
    {
        var result = new List<(string, string)>();
        if (colours == null) return result;
        foreach (var (token, value) in colours.Entries())
        {
            if (value == null) continue;
            if (!CliColor.IsValid(value)) throw new ArgumentException($"invalid portal colour {scheme}.{token}: {value}");
            result.Add((token, value.Trim()));
        }
        return result;
    }

    private static void WriteColours(Utf8JsonWriter json, string scheme, List<(string Token, string Value)> colours)
    {
        json.WriteStartObject(scheme);
        foreach (var (token, value) in colours) json.WriteString(token, value);
        json.WriteEndObject();
    }

    /// <summary>An absolute http(s) URL, or a root-relative path (not <c>//host</c>), without whitespace.</summary>
    private static bool IsWebOrRootRelative(string value)
    {
        if (value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return false;
        if (value[0] == '/') return value.Length == 1 || (value[1] != '/' && value[1] != '\\');
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
