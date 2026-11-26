using System.Text.RegularExpressions;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>
/// The marketplace's plugins folders: a file system error on one becomes a message naming the folder, the reason and
/// what to set, and a folder that cannot be created or written is found at startup (the same messages as the Node and
/// Python servers).
/// </summary>
public static class PluginsFolder
{
    /// <summary>What to do about an unwritable folder below the server's data directory.</summary>
    public const string DataDirHint = "Set DataDir (or QCLI_DATA_DIR) to a writable folder";

    /// <summary>What to do about an unwritable dashboard plugins folder the host named (<c>Marketplace.Dir</c>).</summary>
    public const string MarketplaceDirHint = "Set the marketplace's Dir to a writable folder";

    /// <summary>What to do about an unwritable server plugins folder the host named (<c>ConfigureServerPlugins(o =&gt; o.Dir)</c>).</summary>
    public const string ServerPluginsDirHint = "Set the server plugins' Dir to a writable folder";

    private static readonly Dictionary<string, string> Reasons = new()
    {
        ["EACCES"] = "permission denied",
        ["EPERM"] = "operation not permitted",
        ["EROFS"] = "read-only file system",
        ["ENOSPC"] = "no space left on device",
        ["EDQUOT"] = "disk quota exceeded",
        ["ENOENT"] = "no such file or directory",
        ["ENOTDIR"] = "not a directory",
        ["EISDIR"] = "is a directory",
        ["EEXIST"] = "file already exists",
        ["EMFILE"] = "too many open files",
        ["EBUSY"] = "resource busy or locked",
    };

    private static readonly Regex QuotedPath = new("'([^']+)'", RegexOptions.CultureInvariant);

    /// <summary><c>permission denied (EACCES)</c>: a file system error code in words.</summary>
    public static string Reason(string code) => $"{(Reasons.TryGetValue(code, out var r) ? r : "file system error")} ({code})";

    /// <summary>The message of an unwritable plugins folder, the same on every server.</summary>
    public static string Message(string folder, string code, string hint) =>
        $"cannot write the plugins folder {folder}: {Reason(code)}. {hint}";

    /// <summary>
    /// The message for a file system error on <paramref name="folder"/>, one of its files or one of its parents (a
    /// directory is created with its parents); null for anything else. Only the folder, the error code and the hint
    /// are named: never the exception's own text.
    /// </summary>
    public static string? ErrorFor(Exception exception, string folder, string hint)
    {
        var code = CodeOf(exception);
        if (code == null) return null;
        var root = Path.GetFullPath(folder);
        // The path the exception names, when it names one, must be the folder, inside it or one of its parents
        var match = QuotedPath.Match(exception.Message);
        if (match.Success)
        {
            string path;
            try
            {
                path = Path.GetFullPath(match.Groups[1].Value);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
            if (!Inside(path, root)) return null;
        }
        else if (exception is not UnauthorizedAccessException)
        {
            return null;
        }
        return Message(root, code, hint);
    }

    /// <summary>The errno name of a file system exception; null for any other exception.</summary>
    private static string? CodeOf(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "EACCES",
        DirectoryNotFoundException or FileNotFoundException => "ENOENT",
        PathTooLongException => null,
        IOException io => ErrnoName(io.HResult),
        _ => null,
    };

    private static string? ErrnoName(int hresult)
    {
        // Windows: HRESULT_FROM_WIN32
        if ((hresult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000))
        {
            return (hresult & 0xFFFF) switch
            {
                5 => "EACCES",
                19 => "EROFS",
                32 or 33 => "EBUSY",
                80 or 183 => "EEXIST",
                39 or 112 => "ENOSPC",
                _ => null,
            };
        }
        // Unix: the raw errno
        return hresult switch
        {
            1 => "EPERM",
            13 => "EACCES",
            16 => "EBUSY",
            17 => "EEXIST",
            20 => "ENOTDIR",
            21 => "EISDIR",
            24 => "EMFILE",
            28 => "ENOSPC",
            30 => "EROFS",
            69 when OperatingSystem.IsMacOS() => "EDQUOT",
            122 when OperatingSystem.IsLinux() => "EDQUOT",
            _ => null,
        };
    }

    private static bool Inside(string path, string root)
    {
        var sep = Path.DirectorySeparatorChar;
        path = path.TrimEnd(sep);
        root = root.TrimEnd(sep);
        return path == root || path.StartsWith(root + sep, StringComparison.Ordinal) || root.StartsWith(path + sep, StringComparison.Ordinal);
    }

    /// <summary>
    /// Why <paramref name="folder"/> cannot be created or written: the error code of the folder, or of its nearest
    /// existing parent when it does not exist yet (a file is created and deleted there to find out; the folder itself
    /// is not created); null when it can be.
    /// </summary>
    public static string? UnwritableCode(string folder)
    {
        var path = Path.GetFullPath(folder);
        while (!Directory.Exists(path))
        {
            if (File.Exists(path)) return "ENOTDIR";
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent) || parent == path) return "ENOENT";
            path = parent;
        }
        try
        {
            var probe = Path.Combine(path, $".qcli-write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CodeOf(ex) ?? "EACCES";
        }
    }

    /// <summary>
    /// The startup WARNING for the marketplace folders that cannot be created or written; null when every folder is
    /// fine. Installing, updating and removing plugins would fail with 500.
    /// </summary>
    public static string? UnwritableWarning(IEnumerable<(string Dir, string Hint)> folders)
    {
        var bad = folders
            .Select(f => (Dir: Path.GetFullPath(f.Dir), f.Hint, Code: UnwritableCode(f.Dir)))
            .Where(f => f.Code != null)
            .ToList();
        if (bad.Count == 0) return null;
        var hints = string.Join("; ", bad.Select(f => f.Hint).Distinct());
        var list = string.Join("; ", bad.Select(f => $"{f.Dir}: {Reason(f.Code!)}"));
        return $"the marketplace cannot write the plugins folder{(bad.Count > 1 ? "s" : "")} {list}. Installing, updating and removing plugins will fail. {hints}.";
    }
}
