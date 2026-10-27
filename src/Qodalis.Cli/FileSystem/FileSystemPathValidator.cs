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
    /// Returns the full path if valid, or null if the path is disallowed.
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

        // Symlinks are resolved and roots match whole segments only
        var fullPath = Path.GetFullPath(path);
        return AllowedPathMatcher.IsAllowed(fullPath, _options.AllowedPaths) ? fullPath : null;
    }

    /// <summary>
    /// Returns true if the path is valid and within the allowed paths.
    /// </summary>
    public bool IsAllowed(string? path) => Validate(path) is not null;
}
