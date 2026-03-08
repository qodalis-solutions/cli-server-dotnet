using System.Runtime.InteropServices;

namespace Qodalis.Cli.FileSystem;

public class OsFileStorageProvider : IFileStorageProvider
{
    public string Name => "os";

    private readonly OsProviderOptions _options;

    public OsFileStorageProvider(OsProviderOptions options)
    {
        _options = options;
    }

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

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var resolved = ValidatePath(path);
        return Task.FromResult(File.Exists(resolved) || Directory.Exists(resolved));
    }

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

    public async Task UploadFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        await WriteFileAsync(path, content, ct);
    }

    private string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new FileStoragePermissionError(path ?? "");

        if (path.Contains(".."))
            throw new FileStoragePermissionError(path);

        var fullPath = Path.GetFullPath(path);

        if (_options.AllowedPaths.Count == 0)
            return fullPath;

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
                // Fall through
            }
        }

        return entry.Attributes.ToString();
    }
}
