namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>
/// Where a file of an installed plugin may be written: a plain file name directly in the plugin's folder, on every OS.
/// A package entry such as <c>lib/net8.0/C:appsettings.Production.json</c> keeps a drive on Windows
/// (<c>Path.GetFileName</c> leaves <c>C:appsettings.Production.json</c>, which <c>Path.Combine</c> resolves against the
/// current directory of drive C), so names are checked against Windows' rules everywhere, then the full path must stay
/// in the folder.
/// </summary>
public static class PluginFilePaths
{
    /// <summary>Windows' invalid file-name characters (control characters, <c>"&lt;&gt;:|?*\/</c>) plus this OS's.</summary>
    private static readonly HashSet<char> Invalid =
    [
        .. Enumerable.Range(0, 32).Select(i => (char)i),
        '"', '<', '>', ':', '|', '?', '*', '\\', '/',
        .. Path.GetInvalidFileNameChars(),
    ];

    /// <summary>
    /// True for a plain file name: not empty, at most 255 characters, no character invalid on Windows or this OS (so no
    /// <c>:</c>, no separator), not <c>.</c> or <c>..</c>, not ending with a dot or a space (Windows drops them).
    /// </summary>
    public static bool IsSafeFileName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 255 && name is not ("." or "..")
        && !name.EndsWith('.') && !name.EndsWith(' ') && !name.Any(Invalid.Contains);

    /// <summary>
    /// The full path of <paramref name="name"/> in <paramref name="dir"/>, or null when the name is not a plain file
    /// name or the path would leave the folder.
    /// </summary>
    public static string? Resolve(string dir, string? name)
    {
        if (!IsSafeFileName(name)) return null;
        var root = Path.GetFullPath(dir);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, name!));
        return path.StartsWith(rootWithSeparator, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal) && path.Length > rootWithSeparator.Length
            ? path
            : null;
    }

    /// <summary>
    /// The full path of <paramref name="relative"/> (<c>/</c>-separated, every segment a plain file name, as
    /// <c>runtimes/osx-arm64/native/libe_sqlite3.dylib</c>) in <paramref name="dir"/>, or null when a segment is not a
    /// plain file name or the path would leave the folder.
    /// </summary>
    public static string? ResolveRelative(string dir, string? relative)
    {
        if (string.IsNullOrEmpty(relative)) return null;
        var segments = relative.Split('/');
        if (segments.Length > 8 || !segments.All(IsSafeFileName)) return null;
        var parent = Path.GetFullPath(dir);
        foreach (var folder in segments[..^1])
            parent = Resolve(parent, folder) ?? string.Empty;
        if (parent.Length == 0) return null;
        var path = Resolve(parent, segments[^1]);
        var root = Path.GetFullPath(dir);
        return path != null && path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
    }

    /// <summary>
    /// <see cref="Resolve"/>, or <see cref="DashboardMarketplaceException"/> 502 <c>invalid package: unsafe file name</c>.
    /// </summary>
    public static string ResolveOrThrow(string dir, string? name, string package) =>
        Resolve(dir, name) ?? throw new DashboardMarketplaceException(502, $"invalid package: unsafe file name in {package}: {name}");

    /// <summary><see cref="ResolveRelative"/>, or <see cref="DashboardMarketplaceException"/> 502 <c>invalid package: unsafe file name</c>.</summary>
    public static string ResolveRelativeOrThrow(string dir, string? relative, string package) =>
        ResolveRelative(dir, relative) ?? throw new DashboardMarketplaceException(502, $"invalid package: unsafe file name in {package}: {relative}");
}
