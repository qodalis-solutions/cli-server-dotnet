using System.Runtime.InteropServices;

namespace Qodalis.Cli.Plugin.FileSystem;

/// <summary>
/// Checks whether a path lies inside one of the allowed roots. Both sides are compared
/// after resolving symlinks, and a root only matches whole path segments, so with
/// <c>/srv/share</c> allowed neither <c>/srv/share-other</c> nor a link inside the
/// share that points elsewhere is accepted.
/// </summary>
public static class AllowedPathMatcher
{
    private const int MaxLinkHops = 40;

    // Windows and macOS file systems are case-insensitive by default; Linux is not.
    private static readonly StringComparison Comparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Returns true when <paramref name="path"/>, with its symlinks resolved, is one of
    /// <paramref name="allowedPaths"/> or lies below one of them.
    /// </summary>
    public static bool IsAllowed(string path, IEnumerable<string> allowedPaths)
    {
        var real = ResolveReal(Path.GetFullPath(path));
        if (real is null) return false;

        foreach (var allowedPath in allowedPaths)
        {
            if (string.IsNullOrWhiteSpace(allowedPath)) continue;
            var root = ResolveReal(Path.GetFullPath(allowedPath));
            if (root is not null && IsSameOrBelow(real, root)) return true;
        }
        return false;
    }

    private static bool IsSameOrBelow(string path, string root)
    {
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        if (string.Equals(Path.TrimEndingDirectorySeparator(path), trimmedRoot, Comparison))
            return true;
        // A filesystem root ("/" or "C:\") keeps its separator when trimmed.
        var prefix = Path.EndsInDirectorySeparator(trimmedRoot)
            ? trimmedRoot
            : trimmedRoot + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, Comparison);
    }

    /// <summary>
    /// Like realpath(3): resolves every symlink along an absolute path. Segments that do
    /// not exist yet (a file about to be written) are kept as they are. Returns null for
    /// a link loop.
    /// </summary>
    private static string? ResolveReal(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? "";
        var pending = new Stack<string>(
            fullPath[root.Length..]
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries)
                .Reverse());
        var current = root;
        var hops = 0;

        while (pending.Count > 0)
        {
            var segment = pending.Pop();
            var candidate = Path.Combine(current, segment);

            FileSystemInfo info = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : new FileInfo(candidate);
            if (info.LinkTarget is null)
            {
                current = candidate;
                continue;
            }

            if (++hops > MaxLinkHops) return null;

            // Re-walk the link target, then the rest of the path, from the link's directory.
            var target = Path.GetFullPath(info.LinkTarget, current);
            var targetRoot = Path.GetPathRoot(target) ?? "";
            foreach (var part in target[targetRoot.Length..]
                         .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                             StringSplitOptions.RemoveEmptyEntries)
                         .Reverse())
            {
                pending.Push(part);
            }
            current = targetRoot;
        }

        return current;
    }
}
