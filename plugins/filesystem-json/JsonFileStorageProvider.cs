using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qodalis.Cli.Plugin.FileSystem.Json;

/// <summary>
/// File storage provider that persists the virtual filesystem as a JSON file on disk.
/// The entire file tree is serialized to and deserialized from a single JSON file.
/// </summary>
public class JsonFileStorageProvider : IFileStorageProvider
{
    /// <inheritdoc />
    public string Name => "json-file";

    private readonly JsonFileStorageProviderOptions _options;
    private JsonFileNode _root;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Initializes a new instance, loading the filesystem tree from the configured JSON file if it exists.
    /// </summary>
    /// <param name="options">Options specifying the JSON file path.</param>
    public JsonFileStorageProvider(JsonFileStorageProviderOptions options)
    {
        _options = options;
        _root = Load();
    }

    /// <inheritdoc />
    public Task<List<FileEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var node = Resolve(path);
        if (node == null)
            throw new FileStorageNotFoundError(path);
        if (node.Type != "directory")
            throw new FileStorageNotADirectoryError(path);

        var entries = (node.Children ?? []).Select(child => new FileEntry
        {
            Name = child.Name,
            Type = child.Type,
            Size = child.Size,
            Modified = child.ModifiedAt.ToString("o"),
            Permissions = child.Permissions,
        }).ToList();

        return Task.FromResult(entries);
    }

    /// <inheritdoc />
    public Task<string> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var node = Resolve(path);
        if (node == null)
            throw new FileStorageNotFoundError(path);
        if (node.Type != "file")
            throw new FileStorageIsADirectoryError(path);

        var content = node.ContentBase64 != null
            ? Encoding.UTF8.GetString(Convert.FromBase64String(node.ContentBase64))
            : "";
        return Task.FromResult(content);
    }

    /// <inheritdoc />
    public Task WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        return WriteFileAsync(path, Encoding.UTF8.GetBytes(content), ct);
    }

    /// <inheritdoc />
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

        var children = parent.Children ??= [];
        var existing = children.FirstOrDefault(c => c.Name == fileName);

        if (existing != null)
        {
            if (existing.Type == "directory")
                throw new FileStorageIsADirectoryError(path);

            existing.ContentBase64 = Convert.ToBase64String(content);
            existing.Size = content.Length;
            existing.ModifiedAt = DateTime.UtcNow;
        }
        else
        {
            children.Add(new JsonFileNode
            {
                Name = fileName,
                Type = "file",
                ContentBase64 = Convert.ToBase64String(content),
                Size = content.Length,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
            });
        }

        Save();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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
                var children = current.Children ??= [];
                var child = children.FirstOrDefault(c => c.Name == part);

                if (child != null)
                {
                    if (child.Type != "directory")
                        throw new FileStorageNotADirectoryError(part);
                    current = child;
                }
                else
                {
                    var newDir = new JsonFileNode
                    {
                        Name = part,
                        Type = "directory",
                        Children = [],
                        CreatedAt = DateTime.UtcNow,
                        ModifiedAt = DateTime.UtcNow,
                    };
                    children.Add(newDir);
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

            var children = parent.Children ??= [];
            if (children.Any(c => c.Name == dirName))
                throw new FileStorageExistsError(path);

            children.Add(new JsonFileNode
            {
                Name = dirName,
                Type = "directory",
                Children = [],
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
            });
        }

        Save();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        var parts = SplitPath(path);
        if (parts.Length == 0)
            throw new FileStoragePermissionError(path);

        var parentParts = parts[..^1];
        var name = parts[^1];
        var parent = ResolveOrThrow(parentParts, path);

        var children = parent.Children ??= [];
        var node = children.FirstOrDefault(c => c.Name == name);
        if (node == null)
            throw new FileStorageNotFoundError(path);

        if (node.Type == "directory" && !recursive)
        {
            if ((node.Children?.Count ?? 0) > 0)
                throw new FileStoragePermissionError(path);
        }

        children.Remove(node);
        Save();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
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

        var children = destParent.Children ??= [];
        children.RemoveAll(c => c.Name == destName);
        children.Add(CloneNode(srcNode, destName));

        Save();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MoveAsync(string src, string dest, CancellationToken ct = default)
    {
        var srcParts = SplitPath(src);
        if (srcParts.Length == 0)
            throw new FileStoragePermissionError(src);

        var srcParentParts = srcParts[..^1];
        var srcName = srcParts[^1];
        var srcParent = ResolveOrThrow(srcParentParts, src);

        var srcChildren = srcParent.Children ??= [];
        var srcNode = srcChildren.FirstOrDefault(c => c.Name == srcName);
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

        srcChildren.Remove(srcNode);
        srcNode.Name = destName;
        var destChildren = destParent.Children ??= [];
        destChildren.RemoveAll(c => c.Name == destName);
        destChildren.Add(srcNode);

        Save();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        return Task.FromResult(Resolve(path) != null);
    }

    /// <inheritdoc />
    public Task<Stream> GetDownloadStreamAsync(string path, CancellationToken ct = default)
    {
        var node = Resolve(path);
        if (node == null)
            throw new FileStorageNotFoundError(path);
        if (node.Type != "file")
            throw new FileStorageIsADirectoryError(path);

        var bytes = node.ContentBase64 != null
            ? Convert.FromBase64String(node.ContentBase64)
            : [];
        Stream stream = new MemoryStream(bytes);
        return Task.FromResult(stream);
    }

    /// <inheritdoc />
    public Task UploadFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        return WriteFileAsync(path, content, ct);
    }

    private JsonFileNode Load()
    {
        if (!File.Exists(_options.FilePath))
        {
            return new JsonFileNode
            {
                Name = "/",
                Type = "directory",
                Children = [],
            };
        }

        var json = File.ReadAllText(_options.FilePath);
        return JsonSerializer.Deserialize<JsonFileNode>(json, JsonOptions) ?? new JsonFileNode
        {
            Name = "/",
            Type = "directory",
            Children = [],
        };
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_options.FilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_root, JsonOptions);
        File.WriteAllText(_options.FilePath, json);
    }

    private static string[] SplitPath(string path)
    {
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private JsonFileNode? Resolve(string path)
    {
        var parts = SplitPath(path);
        var current = _root;
        foreach (var part in parts)
        {
            if (current.Type != "directory" || current.Children == null)
                return null;
            var child = current.Children.FirstOrDefault(c => c.Name == part);
            if (child == null)
                return null;
            current = child;
        }
        return current;
    }

    private JsonFileNode ResolveOrThrow(string[] parts, string originalPath)
    {
        var current = _root;
        foreach (var part in parts)
        {
            if (current.Type != "directory" || current.Children == null)
                throw new FileStorageNotFoundError(originalPath);
            var child = current.Children.FirstOrDefault(c => c.Name == part);
            if (child == null)
                throw new FileStorageNotFoundError(originalPath);
            current = child;
        }
        return current;
    }

    private static JsonFileNode CloneNode(JsonFileNode source, string newName)
    {
        var clone = new JsonFileNode
        {
            Name = newName,
            Type = source.Type,
            ContentBase64 = source.ContentBase64,
            Size = source.Size,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            Permissions = source.Permissions,
        };

        if (source.Children != null)
        {
            clone.Children = source.Children
                .Select(child => CloneNode(child, child.Name))
                .ToList();
        }

        return clone;
    }

    internal class JsonFileNode
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "file";
        public string? ContentBase64 { get; set; }
        public List<JsonFileNode>? Children { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
        public long Size { get; set; }
        public string? Permissions { get; set; }
    }
}
