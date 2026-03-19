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
    /// 3. Relative development paths from the content root
    /// </summary>
    public static string? Resolve(string? explicitPath, string contentRootPath)
    {
        // 1. Explicit override
        if (!string.IsNullOrEmpty(explicitPath) && Directory.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        // 2. npm package — search upwards from content root for node_modules (max 5 levels)
        var current = contentRootPath;
        var maxDepth = 5;
        var depth = 0;
        while (current != null && depth < maxDepth)
        {
            var candidate = Path.Combine(current, "node_modules", "@qodalis", "cli-server-dashboard", "dist");
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
            current = Path.GetDirectoryName(current);
            depth++;
        }

        // 3. Relative development path (sibling repo)
        var devPaths = new[]
        {
            Path.Combine(contentRootPath, "..", "cli-server-dashboard", "dist"),
            Path.Combine(contentRootPath, "..", "..", "cli-server-dashboard", "dist"),
        };

        foreach (var path in devPaths)
        {
            if (Directory.Exists(path))
                return Path.GetFullPath(path);
        }

        return null;
    }
}
