using Microsoft.Extensions.FileProviders;

namespace Qodalis.Cli.Plugin.Portal;

/// <summary>
/// Finds the built portal site: <see cref="PortalOptions.SitePath"/> when set (and nothing else), otherwise the npm
/// package <c>node_modules/@qodalis/cli-server-portal/dist</c> near the content root, the sibling
/// <c>cli-server-dashboard/portal/dist</c> repo, then the copy embedded at build time. A site is a folder with an
/// <c>index.html</c>.
/// </summary>
internal static class PortalSiteResolver
{
    private const int MaxDepth = 5;

    public static IFileProvider? Resolve(string? sitePath, string contentRootPath)
    {
        if (!string.IsNullOrEmpty(sitePath))
            return File.Exists(Path.Combine(sitePath, "index.html")) ? new PhysicalFileProvider(Path.GetFullPath(sitePath)) : null;

        var current = contentRootPath;
        for (var depth = 0; current != null && depth < MaxDepth; depth++)
        {
            var candidate = Path.Combine(current, "node_modules", "@qodalis", "cli-server-portal", "dist");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return new PhysicalFileProvider(Path.GetFullPath(candidate));
            current = Path.GetDirectoryName(current);
        }

        current = contentRootPath;
        for (var depth = 0; current != null && depth < MaxDepth; depth++)
        {
            var parent = Path.GetDirectoryName(current);
            if (parent != null)
            {
                var candidate = Path.Combine(parent, "cli-server-dashboard", "portal", "dist");
                if (File.Exists(Path.Combine(candidate, "index.html"))) return new PhysicalFileProvider(Path.GetFullPath(candidate));
            }
            current = parent;
        }

        try
        {
            var embedded = new ManifestEmbeddedFileProvider(typeof(PortalSiteResolver).Assembly, "wwwroot/portal");
            if (embedded.GetFileInfo("index.html").Exists) return embedded;
        }
        catch (Exception)
        {
            // No embedded manifest: the site was not found at build time
        }
        return null;
    }
}
