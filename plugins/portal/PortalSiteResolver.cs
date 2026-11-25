using Microsoft.Extensions.FileProviders;

namespace Qodalis.Cli.Plugin.Portal;

/// <summary>Where the portal site was found (<c>source</c> of <c>portal --json</c>).</summary>
public static class PortalSiteSources
{
    /// <summary><see cref="PortalOptions.SitePath"/>.</summary>
    public const string SiteDir = "siteDir";
    /// <summary>The npm package <c>@qodalis/cli-server-portal</c> near the content root.</summary>
    public const string Package = "package";
    /// <summary>The sibling <c>cli-server-dashboard/portal/dist</c> (a development checkout).</summary>
    public const string Checkout = "checkout";
    /// <summary>The copy embedded in the plugin at build time.</summary>
    public const string Bundled = "bundled";

    /// <summary>The source as the <c>portal</c> command shows it.</summary>
    public static string Title(string source) => source switch
    {
        SiteDir => "SitePath",
        Package => "@qodalis/cli-server-portal",
        Checkout => "cli-server-dashboard/portal/dist",
        Bundled => "bundled with the plugin",
        _ => source,
    };
}

/// <summary>A found portal site: its files, where it was found (<see cref="PortalSiteSources"/>) and its folder (null when embedded).</summary>
internal sealed record PortalSite(IFileProvider Files, string Source, string? Directory);

/// <summary>
/// Finds the built portal site: <see cref="PortalOptions.SitePath"/> when set (and nothing else), otherwise the npm
/// package <c>node_modules/@qodalis/cli-server-portal/dist</c> near the content root, the sibling
/// <c>cli-server-dashboard/portal/dist</c> repo, then the copy embedded at build time. A site is a folder with an
/// <c>index.html</c>.
/// </summary>
internal static class PortalSiteResolver
{
    private const int MaxDepth = 5;

    private static PortalSite Physical(string dir, string source)
    {
        var full = Path.GetFullPath(dir);
        return new PortalSite(new PhysicalFileProvider(full), source, full);
    }

    public static PortalSite? Resolve(string? sitePath, string contentRootPath)
    {
        if (!string.IsNullOrEmpty(sitePath))
            return File.Exists(Path.Combine(sitePath, "index.html")) ? Physical(sitePath, PortalSiteSources.SiteDir) : null;

        var current = contentRootPath;
        for (var depth = 0; current != null && depth < MaxDepth; depth++)
        {
            var candidate = Path.Combine(current, "node_modules", "@qodalis", "cli-server-portal", "dist");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return Physical(candidate, PortalSiteSources.Package);
            current = Path.GetDirectoryName(current);
        }

        current = contentRootPath;
        for (var depth = 0; current != null && depth < MaxDepth; depth++)
        {
            var parent = Path.GetDirectoryName(current);
            if (parent != null)
            {
                var candidate = Path.Combine(parent, "cli-server-dashboard", "portal", "dist");
                if (File.Exists(Path.Combine(candidate, "index.html"))) return Physical(candidate, PortalSiteSources.Checkout);
            }
            current = parent;
        }

        try
        {
            var embedded = new ManifestEmbeddedFileProvider(typeof(PortalSiteResolver).Assembly, "wwwroot/portal");
            if (embedded.GetFileInfo("index.html").Exists) return new PortalSite(embedded, PortalSiteSources.Bundled, null);
        }
        catch (Exception)
        {
            // No embedded manifest: the site was not found at build time
        }
        return null;
    }
}
