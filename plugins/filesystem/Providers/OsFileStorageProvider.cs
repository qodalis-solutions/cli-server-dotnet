using System.Runtime.InteropServices;

namespace Qodalis.Cli.Plugin.FileSystem;

/// <summary>
/// File storage provider backed by the host operating system's filesystem.
/// All paths are validated against a configurable allowlist to prevent unauthorized access.
/// </summary>
public class OsFileStorageProvider : IFileStorageProvider
{
    /// <inheritdoc />
    public string Name => "os";

    private readonly OsProviderOptions _options;

    /// <summary>
    /// Initializes a new instance with the specified options.
    /// </summary>
    /// <param name="options">Options controlling allowed paths and provider behavior.</param>
    public OsFileStorageProvider(OsProviderOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public Task<List<FileEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var resolved = ValidatePath(path);
        if (!Directory.Exists(resolved))
            throw new FileStorageNotFoundError(path);

        var dirInfo = new DirectoryInfo(resolved);
        var entries = dirInfo.EnumerateFileSystemInfos()
            .Select(entry => new FileEntry
            {
                Name = entry.Name,
                Type = entry is DirectoryInfo ? "directory" : "file",
                Size = entry is FileInfo fi ? fi.Length : 0L,
                Modified = entry.LastWriteTimeUtc.ToString("o"),
                Permissions = GetPermissions(entry),
            })
            .ToList();

        return Task.FromResult(entries);
    }

    /// <inheritdoc />
    public async Task<string> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var resolved = ValidatePath(path);
        if (!File.Exists(resolved))
        {
            if (Directory.Exists(resolved))
                throw new FileStorageIsADirectoryError(path);
            throw new FileStorageNotFoundError(path);
        }

        return await File.ReadAllTextAsync(resolved, ct);
    }

    /// <inheritdoc />
    public async Task WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        var resolved = ValidatePath(path);
        var dir = Path.GetDirectoryName(resolved);
        if (dir != null && !Directory.Exists(dir))
            throw new FileStorageNotFoundError(Path.GetDirectoryName(path) ?? path);

        if (Directory.Exists(resolved))
            throw new FileStorageIsADirectoryError(path);

        await File.WriteAllTextAsync(resolved, content, ct);
    }

    /// <inheritdoc />
    public async Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        var resolved = ValidatePath(path);
        var dir = Path.GetDirectoryName(resolved);
        if (dir != null && !Directory.Exists(dir))
            throw new FileStorageNotFoundError(Path.GetDirectoryName(path) ?? path);

        if (Directory.Exists(resolved))
            throw new FileStorageIsADirectoryError(path);

        await File.WriteAllBytesAsync(resolved, content, ct);
    }

    /// <inheritdoc />
    public Task<FileStat> StatAsync(string path, CancellationToken ct = default)
    {
        var resolved = ValidatePath(path);

        if (File.Exists(resolved))
        {
            var fi = new FileInfo(resolved);
            return Task.FromResult(new FileStat
            {
                Name = fi.Name,
                Type = "file",
                Size = fi.Length,
                Created = fi.CreationTimeUtc.ToString("o"),
                Modified = fi.LastWriteTimeUtc.ToString("o"),
                Permissions = GetPermissions(fi),
            });
        }

        if (Directory.Exists(resolved))
        {
            var di = new DirectoryInfo(resolved);
            return Task.FromResult(new FileStat
            {
                Name = di.Name,
                Type = "directory",
                Size = 0L,
                Created = di.CreationTimeUtc.ToString("o"),
                Modified = di.LastWriteTimeUtc.ToString("o"),
                Permissions = GetPermissions(di),
            });
        }

        throw new FileStorageNotFoundError(path);
    }

    /// <inheritdoc />
    public Task MkdirAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        var resolved = ValidatePath(path);

        if (Directory.Exists(resolved))
            throw new FileStorageExistsError(path);

        if (recursive)
        {
            Directory.CreateDirectory(resolved);
        }
        else
        {
            var parent = Path.GetDirectoryName(resolved);
            if (parent != null && !Directory.Exists(parent))
                throw new FileStorageNotFoundError(path);
            Directory.CreateDirectory(resolved);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        var resolved = ValidatePath(path);

        if (File.Exists(resolved))
        {
            File.Delete(resolved);
            return Task.CompletedTask;
        }

        if (Directory.Exists(resolved))
        {
            Directory.Delete(resolved, recursive);
            return Task.CompletedTask;
        }

        throw new FileStorageNotFoundError(path);
    }

    /// <inheritdoc />
    public Task CopyAsync(string src, string dest, CancellationToken ct = default)
    {
        var resolvedSrc = ValidatePath(src);
        var resolvedDest = ValidatePath(dest);

        if (!File.Exists(resolvedSrc))
        {
            if (Directory.Exists(resolvedSrc))
                throw new FileStorageIsADirectoryError(src);
            throw new FileStorageNotFoundError(src);
        }

        File.Copy(resolvedSrc, resolvedDest, overwrite: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MoveAsync(string src, string dest, CancellationToken ct = default)
    {
        var resolvedSrc = ValidatePath(src);
        var resolvedDest = ValidatePath(dest);

        if (File.Exists(resolvedSrc))
        {
            File.Move(resolvedSrc, resolvedDest, overwrite: true);
            return Task.CompletedTask;
        }

        if (Directory.Exists(resolvedSrc))
        {
            Directory.Move(resolvedSrc, resolvedDest);
            return Task.CompletedTask;
        }

        throw new FileStorageNotFoundError(src);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var resolved = ValidatePath(path);
        return Task.FromResult(File.Exists(resolved) || Directory.Exists(resolved));
    }

    /// <inheritdoc />
    public Task<Stream> GetDownloadStreamAsync(string path, CancellationToken ct = default)
    {
        var resolved = ValidatePath(path);
        if (!File.Exists(resolved))
        {
            if (Directory.Exists(resolved))
                throw new FileStorageIsADirectoryError(path);
            throw new FileStorageNotFoundError(path);
        }

        Stream stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(stream);
    }

    /// <inheritdoc />
    public async Task UploadFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        await WriteFileAsync(path, content, ct);
    }

    private string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new FileStoragePermissionError(path ?? "");

        if (_options.AllowedPaths.Count == 0)
            throw new FileStoragePermissionError("No allowed paths configured.");

        var fullPath = Path.GetFullPath(path);

        foreach (var allowedPath in _options.AllowedPaths)
        {
            var resolvedAllowed = Path.GetFullPath(allowedPath);
            if (fullPath.StartsWith(resolvedAllowed, StringComparison.OrdinalIgnoreCase))
                return fullPath;
        }

        throw new FileStoragePermissionError(path);
    }

    private static string GetPermissions(FileSystemInfo entry)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                return entry.UnixFileMode.ToString();
            }
            catch
            {
                // Unix file mode not available on this platform; fall through to attributes.
            }
        }

        return entry.Attributes.ToString();
    }
}
