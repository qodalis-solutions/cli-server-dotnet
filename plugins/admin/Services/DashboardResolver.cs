namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>
/// Resolves the dashboard dist directory at runtime.
/// </summary>
public static class DashboardResolver
{
    /// <summary>
    /// Resolves the dashboard dist directory. Looks for:
    /// 1. Explicitly configured path
    /// 2. node_modules/@qodalis/cli-server-dashboard/dist (npm package installed nearby)
    /// 3. Sibling cli-server-dashboard repo (walks up from content root)
    /// </summary>
    public static string? Resolve(string? explicitPath, string contentRootPath)
    {
        // 1. Explicit override
        if (!string.IsNullOrEmpty(explicitPath) && Directory.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        // 2. npm package — search upwards from content root for node_modules (max 5 levels)
        var current = contentRootPath;
        for (var depth = 0; current != null && depth < 5; depth++)
        {
            var candidate = Path.Combine(current, "node_modules", "@qodalis", "cli-server-dashboard", "dist");
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
            current = Path.GetDirectoryName(current);
        }

        // 3. Sibling repo — walk up from content root looking for cli-server-dashboard/dist
        current = contentRootPath;
        for (var depth = 0; current != null && depth < 5; depth++)
        {
            var parent = Path.GetDirectoryName(current);
            if (parent != null)
            {
                var candidate = Path.Combine(parent, "cli-server-dashboard", "dist");
                if (Directory.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            current = parent;
        }

        return null;
    }
}
