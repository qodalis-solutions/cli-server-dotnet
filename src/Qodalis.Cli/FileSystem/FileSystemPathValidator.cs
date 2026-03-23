using Qodalis.Cli.Plugin.FileSystem;

namespace Qodalis.Cli.FileSystem;

/// <summary>
/// Validates that filesystem paths are within the configured whitelist to prevent unauthorized access.
/// </summary>
public class FileSystemPathValidator
{
    private readonly FileSystemOptions _options;

    /// <summary>
    /// Initializes a new instance of <see cref="FileSystemPathValidator"/>.
    /// </summary>
    /// <param name="options">The filesystem options containing the allowed paths whitelist.</param>
    public FileSystemPathValidator(FileSystemOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Validates that the given path is within the allowed paths whitelist.
    /// Returns the resolved full path if valid, or null if the path is disallowed.
    /// </summary>
    public string? Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // Block path traversal attempts
        if (path.Contains(".."))
        {
            return null;
        }

        // Resolve the full path
        var fullPath = Path.GetFullPath(path);

        // Resolve symlinks if the path exists
        if (Path.Exists(fullPath))
        {
            fullPath = ResolveSymlinks(fullPath);
        }

        // Check the resolved path starts with one of the allowed paths
        foreach (var allowedPath in _options.AllowedPaths)
        {
            var resolvedAllowed = Path.GetFullPath(allowedPath);
            if (fullPath.StartsWith(resolvedAllowed, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true if the path is valid and within the allowed paths.
    /// </summary>
    public bool IsAllowed(string? path) => Validate(path) is not null;

    private static string ResolveSymlinks(string path)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.LinkTarget is not null)
        {
            var resolved = Path.GetFullPath(fileInfo.LinkTarget, Path.GetDirectoryName(path)!);
            return resolved;
        }

        var dirInfo = new DirectoryInfo(path);
        if (dirInfo.LinkTarget is not null)
        {
            var resolved = Path.GetFullPath(dirInfo.LinkTarget, Path.GetDirectoryName(path)!);
            return resolved;
        }

        return path;
    }
}
