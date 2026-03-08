using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Qodalis.Cli.FileSystem.EfCore;

public class EfCoreFileStorageProvider : IFileStorageProvider, IDisposable
{
    public string Name => "efcore";

    private readonly FileStorageDbContext _db;

    public EfCoreFileStorageProvider(FileStorageDbContext db)
    {
        _db = db;
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    public async Task<List<FileEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);

        if (normalizedPath != "/")
        {
            var node = await _db.Files.FirstOrDefaultAsync(f => f.Path == normalizedPath, ct);
            if (node == null)
                throw new FileStorageNotFoundError(path);
            if (node.Type != "directory")
                throw new FileStorageNotADirectoryError(path);
        }

        var entries = await _db.Files
            .Where(f => f.ParentPath == normalizedPath)
            .OrderBy(f => f.Name)
            .Select(f => new FileEntry
            {
                Name = f.Name,
                Type = f.Type,
                Size = f.Size,
                Modified = f.ModifiedAt.ToString("o"),
                Permissions = f.Permissions,
            })
            .ToListAsync(ct);

        return entries;
    }

    public async Task<string> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        var node = await _db.Files.FirstOrDefaultAsync(f => f.Path == normalizedPath, ct);
        if (node == null)
            throw new FileStorageNotFoundError(path);
        if (node.Type != "file")
            throw new FileStorageIsADirectoryError(path);

        var content = node.Content != null ? Encoding.UTF8.GetString(node.Content) : "";
        return content;
    }

    public Task WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        return WriteFileAsync(path, Encoding.UTF8.GetBytes(content), ct);
    }

    public async Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        var parentPath = GetParentPath(normalizedPath);
        var fileName = GetName(normalizedPath);

        if (parentPath != "/")
        {
            var parent = await _db.Files.FirstOrDefaultAsync(f => f.Path == parentPath, ct);
            if (parent == null)
                throw new FileStorageNotFoundError(path);
            if (parent.Type != "directory")
                throw new FileStorageNotADirectoryError(parentPath);
        }

        var existing = await _db.Files.FirstOrDefaultAsync(f => f.Path == normalizedPath, ct);
        if (existing != null)
        {
            if (existing.Type == "directory")
                throw new FileStorageIsADirectoryError(path);

            existing.Content = content;
            existing.Size = content.Length;
            existing.ModifiedAt = DateTime.UtcNow;
        }
        else
        {
            _db.Files.Add(new FileEntity
            {
                Path = normalizedPath,
                Name = fileName,
                Type = "file",
                Content = content,
                Size = content.Length,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                ParentPath = parentPath,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<FileStat> StatAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        var node = await _db.Files.FirstOrDefaultAsync(f => f.Path == normalizedPath, ct);
        if (node == null)
            throw new FileStorageNotFoundError(path);

        return new FileStat
        {
            Name = node.Name,
            Type = node.Type,
            Size = node.Size,
            Created = node.CreatedAt.ToString("o"),
            Modified = node.ModifiedAt.ToString("o"),
            Permissions = node.Permissions,
        };
    }

    public async Task MkdirAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath == "/")
            return;

        if (recursive)
        {
            var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var currentPath = "";
            foreach (var part in parts)
            {
                var parentForThis = currentPath == "" ? "/" : "/" + currentPath;
                currentPath = currentPath == "" ? part : currentPath + "/" + part;
                var fullPath = "/" + currentPath;

                var existing = await _db.Files.FirstOrDefaultAsync(f => f.Path == fullPath, ct);
                if (existing != null)
                {
                    if (existing.Type != "directory")
                        throw new FileStorageNotADirectoryError(part);
                    continue;
                }

                _db.Files.Add(new FileEntity
                {
                    Path = fullPath,
                    Name = part,
                    Type = "directory",
                    Size = 0,
                    CreatedAt = DateTime.UtcNow,
                    ModifiedAt = DateTime.UtcNow,
                    ParentPath = parentForThis,
                });
            }
        }
        else
        {
            var parentPath = GetParentPath(normalizedPath);
            var dirName = GetName(normalizedPath);

            if (parentPath != "/")
            {
                var parent = await _db.Files.FirstOrDefaultAsync(f => f.Path == parentPath, ct);
                if (parent == null)
                    throw new FileStorageNotFoundError(path);
                if (parent.Type != "directory")
                    throw new FileStorageNotADirectoryError(parentPath);
            }

            var existing = await _db.Files.FirstOrDefaultAsync(f => f.Path == normalizedPath, ct);
            if (existing != null)
                throw new FileStorageExistsError(path);

            _db.Files.Add(new FileEntity
            {
                Path = normalizedPath,
                Name = dirName,
                Type = "directory",
                Size = 0,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                ParentPath = parentPath,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath == "/")
            throw new FileStoragePermissionError(path);

        var node = await _db.Files.FirstOrDefaultAsync(f => f.Path == normalizedPath, ct);
        if (node == null)
            throw new FileStorageNotFoundError(path);

        if (node.Type == "directory" && !recursive)
        {
            var hasChildren = await _db.Files.AnyAsync(f => f.ParentPath == normalizedPath, ct);
            if (hasChildren)
                throw new FileStoragePermissionError(path);
        }

        if (recursive)
        {
            var prefix = normalizedPath + "/";
            var descendants = await _db.Files
                .Where(f => f.Path == normalizedPath || f.Path.StartsWith(prefix))
                .ToListAsync(ct);
            _db.Files.RemoveRange(descendants);
        }
        else
        {
            _db.Files.Remove(node);
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task CopyAsync(string src, string dest, CancellationToken ct = default)
    {
        var srcPath = NormalizePath(src);
        var destPath = NormalizePath(dest);

        var srcNode = await _db.Files.FirstOrDefaultAsync(f => f.Path == srcPath, ct);
        if (srcNode == null)
            throw new FileStorageNotFoundError(src);

        var destParent = GetParentPath(destPath);
        var destName = GetName(destPath);

        if (destParent != "/")
        {
            var parent = await _db.Files.FirstOrDefaultAsync(f => f.Path == destParent, ct);
            if (parent == null)
                throw new FileStorageNotFoundError(dest);
            if (parent.Type != "directory")
                throw new FileStorageNotADirectoryError(destParent);
        }

        var now = DateTime.UtcNow;

        // Remove existing destination if present
        var existingDest = await _db.Files.FirstOrDefaultAsync(f => f.Path == destPath, ct);
        if (existingDest != null)
            _db.Files.Remove(existingDest);

        _db.Files.Add(new FileEntity
        {
            Path = destPath,
            Name = destName,
            Type = srcNode.Type,
            Content = srcNode.Content != null ? (byte[])srcNode.Content.Clone() : null,
            Size = srcNode.Size,
            Permissions = srcNode.Permissions,
            CreatedAt = now,
            ModifiedAt = now,
            ParentPath = destParent,
        });

        // If source is a directory, copy descendants too
        if (srcNode.Type == "directory")
        {
            var srcPrefix = srcPath + "/";
            var descendants = await _db.Files
                .Where(f => f.Path.StartsWith(srcPrefix))
                .ToListAsync(ct);

            foreach (var descendant in descendants)
            {
                var relativePath = descendant.Path[srcPath.Length..];
                var newPath = destPath + relativePath;
                var newParent = GetParentPath(newPath);

                _db.Files.Add(new FileEntity
                {
                    Path = newPath,
                    Name = descendant.Name,
                    Type = descendant.Type,
                    Content = descendant.Content != null ? (byte[])descendant.Content.Clone() : null,
                    Size = descendant.Size,
                    Permissions = descendant.Permissions,
                    CreatedAt = now,
                    ModifiedAt = now,
                    ParentPath = newParent,
                });
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task MoveAsync(string src, string dest, CancellationToken ct = default)
    {
        var srcPath = NormalizePath(src);
        var destPath = NormalizePath(dest);

        var srcNode = await _db.Files.FirstOrDefaultAsync(f => f.Path == srcPath, ct);
        if (srcNode == null)
            throw new FileStorageNotFoundError(src);

        var destParent = GetParentPath(destPath);
        var destName = GetName(destPath);

        if (destParent != "/")
        {
            var parent = await _db.Files.FirstOrDefaultAsync(f => f.Path == destParent, ct);
            if (parent == null)
                throw new FileStorageNotFoundError(dest);
            if (parent.Type != "directory")
                throw new FileStorageNotADirectoryError(destParent);
        }

        // Move descendants first if it's a directory
        if (srcNode.Type == "directory")
        {
            var srcPrefix = srcPath + "/";
            var descendants = await _db.Files
                .Where(f => f.Path.StartsWith(srcPrefix))
                .ToListAsync(ct);

            foreach (var descendant in descendants)
            {
                var relativePath = descendant.Path[srcPath.Length..];
                descendant.Path = destPath + relativePath;
                descendant.ParentPath = GetParentPath(descendant.Path);
                descendant.ModifiedAt = DateTime.UtcNow;
            }
        }

        srcNode.Path = destPath;
        srcNode.Name = destName;
        srcNode.ParentPath = destParent;
        srcNode.ModifiedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        return await _db.Files.AnyAsync(f => f.Path == normalizedPath, ct);
    }

    public async Task<Stream> GetDownloadStreamAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        var node = await _db.Files.FirstOrDefaultAsync(f => f.Path == normalizedPath, ct);
        if (node == null)
            throw new FileStorageNotFoundError(path);
        if (node.Type != "file")
            throw new FileStorageIsADirectoryError(path);

        return new MemoryStream(node.Content ?? []);
    }

    public Task UploadFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        return WriteFileAsync(path, content, ct);
    }

    // --- Helpers ---

    private static string NormalizePath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "/";
        return "/" + string.Join("/", parts);
    }

    private static string GetParentPath(string normalizedPath)
    {
        var lastSlash = normalizedPath.LastIndexOf('/');
        if (lastSlash <= 0)
            return "/";
        return normalizedPath[..lastSlash];
    }

    private static string GetName(string normalizedPath)
    {
        var lastSlash = normalizedPath.LastIndexOf('/');
        return normalizedPath[(lastSlash + 1)..];
    }
}
