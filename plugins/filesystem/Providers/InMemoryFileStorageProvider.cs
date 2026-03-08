using System.Text;

namespace Qodalis.Cli.FileSystem;

public class InMemoryFileStorageProvider : IFileStorageProvider
{
    public string Name => "in-memory";

    private readonly FileNode _root;

    public InMemoryFileStorageProvider()
    {
        _root = new FileNode
        {
            Name = "/",
            Type = "directory",
            Children = new Dictionary<string, FileNode>()
        };
    }

    public Task<List<FileEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var node = Resolve(path);
        if (node == null)
            throw new FileStorageNotFoundError(path);
        if (node.Type != "directory")
            throw new FileStorageNotADirectoryError(path);

        var entries = node.Children!.Values.Select(child => new FileEntry
        {
            Name = child.Name,
            Type = child.Type,
            Size = child.Size,
            Modified = child.ModifiedAt.ToString("o"),
            Permissions = child.Permissions,
        }).ToList();

        return Task.FromResult(entries);
    }

    public Task<string> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var node = Resolve(path);
        if (node == null)
            throw new FileStorageNotFoundError(path);
        if (node.Type != "file")
            throw new FileStorageIsADirectoryError(path);

        var content = node.Content != null ? Encoding.UTF8.GetString(node.Content) : "";
        return Task.FromResult(content);
    }

    public Task WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        return WriteFileAsync(path, Encoding.UTF8.GetBytes(content), ct);
    }

    public Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        var parts = SplitPath(path);
        if (parts.Length == 0)
            throw new FileStoragePermissionError(path);

        var parentParts = parts[..^1];
        var fileName = parts[^1];
        var parent = ResolveOrThrow(parentParts, path);

        if (parent.Type != "directory")
            throw new FileStorageNotADirectoryError(string.Join("/", parentParts));

        if (parent.Children!.TryGetValue(fileName, out var existing))
        {
            if (existing.Type == "directory")
                throw new FileStorageIsADirectoryError(path);

            existing.Content = content;
            existing.Size = content.Length;
            existing.ModifiedAt = DateTime.UtcNow;
        }
        else
        {
            parent.Children[fileName] = new FileNode
            {
                Name = fileName,
                Type = "file",
                Content = content,
                Size = content.Length,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
            };
        }

        return Task.CompletedTask;
    }

    public Task<FileStat> StatAsync(string path, CancellationToken ct = default)
    {
        var node = Resolve(path);
        if (node == null)
            throw new FileStorageNotFoundError(path);

        var stat = new FileStat
        {
            Name = node.Name,
            Type = node.Type,
            Size = node.Size,
            Created = node.CreatedAt.ToString("o"),
            Modified = node.ModifiedAt.ToString("o"),
            Permissions = node.Permissions,
        };

        return Task.FromResult(stat);
    }

    public Task MkdirAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        var parts = SplitPath(path);
        if (parts.Length == 0)
            return Task.CompletedTask;

        if (recursive)
        {
            var current = _root;
            foreach (var part in parts)
            {
                if (current.Children!.TryGetValue(part, out var child))
                {
                    if (child.Type != "directory")
                        throw new FileStorageNotADirectoryError(part);
                    current = child;
                }
                else
                {
                    var newDir = new FileNode
                    {
                        Name = part,
                        Type = "directory",
                        Children = new Dictionary<string, FileNode>(),
                        CreatedAt = DateTime.UtcNow,
                        ModifiedAt = DateTime.UtcNow,
                    };
                    current.Children[part] = newDir;
                    current = newDir;
                }
            }
        }
        else
        {
            var parentParts = parts[..^1];
            var dirName = parts[^1];
            var parent = ResolveOrThrow(parentParts, path);

            if (parent.Type != "directory")
                throw new FileStorageNotADirectoryError(string.Join("/", parentParts));

            if (parent.Children!.ContainsKey(dirName))
                throw new FileStorageExistsError(path);

            parent.Children[dirName] = new FileNode
            {
                Name = dirName,
                Type = "directory",
                Children = new Dictionary<string, FileNode>(),
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
            };
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        var parts = SplitPath(path);
        if (parts.Length == 0)
            throw new FileStoragePermissionError(path);

        var parentParts = parts[..^1];
        var name = parts[^1];
        var parent = ResolveOrThrow(parentParts, path);

        if (!parent.Children!.TryGetValue(name, out var node))
            throw new FileStorageNotFoundError(path);

        if (node.Type == "directory" && !recursive)
        {
            if (node.Children!.Count > 0)
                throw new FileStoragePermissionError(path);
        }

        parent.Children.Remove(name);
        return Task.CompletedTask;
    }

    public Task CopyAsync(string src, string dest, CancellationToken ct = default)
    {
        var srcNode = Resolve(src);
        if (srcNode == null)
            throw new FileStorageNotFoundError(src);

        var destParts = SplitPath(dest);
        if (destParts.Length == 0)
            throw new FileStoragePermissionError(dest);

        var destParentParts = destParts[..^1];
        var destName = destParts[^1];
        var destParent = ResolveOrThrow(destParentParts, dest);

        if (destParent.Type != "directory")
            throw new FileStorageNotADirectoryError(string.Join("/", destParentParts));

        destParent.Children![destName] = CloneNode(srcNode, destName);
        return Task.CompletedTask;
    }

    public Task MoveAsync(string src, string dest, CancellationToken ct = default)
    {
        var srcParts = SplitPath(src);
        if (srcParts.Length == 0)
            throw new FileStoragePermissionError(src);

        var srcParentParts = srcParts[..^1];
        var srcName = srcParts[^1];
        var srcParent = ResolveOrThrow(srcParentParts, src);

        if (!srcParent.Children!.TryGetValue(srcName, out var srcNode))
            throw new FileStorageNotFoundError(src);

        var destParts = SplitPath(dest);
        if (destParts.Length == 0)
            throw new FileStoragePermissionError(dest);

        var destParentParts = destParts[..^1];
        var destName = destParts[^1];
        var destParent = ResolveOrThrow(destParentParts, dest);

        if (destParent.Type != "directory")
            throw new FileStorageNotADirectoryError(string.Join("/", destParentParts));

        srcParent.Children.Remove(srcName);
        srcNode.Name = destName;
        destParent.Children![destName] = srcNode;
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        return Task.FromResult(Resolve(path) != null);
    }

    public Task<Stream> GetDownloadStreamAsync(string path, CancellationToken ct = default)
    {
        var node = Resolve(path);
        if (node == null)
            throw new FileStorageNotFoundError(path);
        if (node.Type != "file")
            throw new FileStorageIsADirectoryError(path);

        Stream stream = new MemoryStream(node.Content ?? []);
        return Task.FromResult(stream);
    }

    public Task UploadFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        return WriteFileAsync(path, content, ct);
    }

    private static string[] SplitPath(string path)
    {
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private FileNode? Resolve(string path)
    {
        var parts = SplitPath(path);
        var current = _root;
        foreach (var part in parts)
        {
            if (current.Type != "directory" || current.Children == null)
                return null;
            if (!current.Children.TryGetValue(part, out var child))
                return null;
            current = child;
        }
        return current;
    }

    private FileNode ResolveOrThrow(string[] parts, string originalPath)
    {
        var current = _root;
        foreach (var part in parts)
        {
            if (current.Type != "directory" || current.Children == null)
                throw new FileStorageNotFoundError(originalPath);
            if (!current.Children.TryGetValue(part, out var child))
                throw new FileStorageNotFoundError(originalPath);
            current = child;
        }
        return current;
    }

    private static FileNode CloneNode(FileNode source, string newName)
    {
        var clone = new FileNode
        {
            Name = newName,
            Type = source.Type,
            Content = source.Content != null ? (byte[])source.Content.Clone() : null,
            Size = source.Size,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            Permissions = source.Permissions,
        };

        if (source.Children != null)
        {
            clone.Children = new Dictionary<string, FileNode>();
            foreach (var (key, child) in source.Children)
            {
                clone.Children[key] = CloneNode(child, child.Name);
            }
        }

        return clone;
    }

    private class FileNode
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "file";
        public byte[]? Content { get; set; }
        public Dictionary<string, FileNode>? Children { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
        public long Size { get; set; }
        public string? Permissions { get; set; }
    }
}
