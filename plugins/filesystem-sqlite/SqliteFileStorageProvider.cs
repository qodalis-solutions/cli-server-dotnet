using System.Text;
using Microsoft.Data.Sqlite;

namespace Qodalis.Cli.FileSystem.Sqlite;

public class SqliteFileStorageProvider : IFileStorageProvider, IDisposable
{
    public string Name => "sqlite";

    private readonly SqliteProviderOptions _options;
    private readonly string _connectionString;
    // Keep a persistent connection for in-memory databases so the DB survives across calls
    private readonly SqliteConnection? _keepAliveConnection;

    public SqliteFileStorageProvider(SqliteProviderOptions options)
    {
        _options = options;
        var isMemory = _options.DbPath == ":memory:" || _options.DbPath.Contains("mode=memory");

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DbPath,
            Mode = isMemory ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        if (isMemory)
        {
            // For in-memory databases, keep a connection open so the shared cache DB persists
            _keepAliveConnection = new SqliteConnection(_connectionString);
            _keepAliveConnection.Open();
        }

        InitializeDatabase();
    }

    public void Dispose()
    {
        _keepAliveConnection?.Dispose();
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void InitializeDatabase()
    {
        // Ensure parent directory exists for file-based databases
        var isMemory = _options.DbPath == ":memory:" || _options.DbPath.Contains("mode=memory");
        if (!isMemory)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_options.DbPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                type TEXT NOT NULL CHECK(type IN ('file', 'directory')),
                content BLOB,
                size INTEGER NOT NULL DEFAULT 0,
                permissions TEXT DEFAULT '644',
                created_at TEXT NOT NULL,
                modified_at TEXT NOT NULL,
                parent_path TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_files_parent ON files(parent_path);
        ";
        cmd.ExecuteNonQuery();
    }

    public Task<List<FileEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);

        // Check the path exists and is a directory
        if (normalizedPath != "/")
        {
            var node = GetNode(normalizedPath);
            if (node == null)
                throw new FileStorageNotFoundError(path);
            if (node.Type != "directory")
                throw new FileStorageNotADirectoryError(path);
        }

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, type, size, modified_at, permissions FROM files WHERE parent_path = @parent";
        cmd.Parameters.AddWithValue("@parent", normalizedPath);

        var entries = new List<FileEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new FileEntry
            {
                Name = reader.GetString(0),
                Type = reader.GetString(1),
                Size = reader.GetInt64(2),
                Modified = reader.GetString(3),
                Permissions = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }

        return Task.FromResult(entries);
    }

    public Task<string> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        var node = GetNode(normalizedPath);
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
        var normalizedPath = NormalizePath(path);
        var parentPath = GetParentPath(normalizedPath);
        var fileName = GetName(normalizedPath);

        // Verify parent exists
        if (parentPath != "/")
        {
            var parent = GetNode(parentPath);
            if (parent == null)
                throw new FileStorageNotFoundError(path);
            if (parent.Type != "directory")
                throw new FileStorageNotADirectoryError(parentPath);
        }

        var existing = GetNode(normalizedPath);
        if (existing != null)
        {
            if (existing.Type == "directory")
                throw new FileStorageIsADirectoryError(path);

            // Update existing file
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE files SET content = @content, size = @size, modified_at = @modified WHERE path = @path";
            cmd.Parameters.AddWithValue("@content", content);
            cmd.Parameters.AddWithValue("@size", content.Length);
            cmd.Parameters.AddWithValue("@modified", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@path", normalizedPath);
            cmd.ExecuteNonQuery();
        }
        else
        {
            // Insert new file
            var now = DateTime.UtcNow.ToString("o");
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO files (path, name, type, content, size, created_at, modified_at, parent_path)
                VALUES (@path, @name, 'file', @content, @size, @created, @modified, @parent)";
            cmd.Parameters.AddWithValue("@path", normalizedPath);
            cmd.Parameters.AddWithValue("@name", fileName);
            cmd.Parameters.AddWithValue("@content", content);
            cmd.Parameters.AddWithValue("@size", content.Length);
            cmd.Parameters.AddWithValue("@created", now);
            cmd.Parameters.AddWithValue("@modified", now);
            cmd.Parameters.AddWithValue("@parent", parentPath);
            cmd.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task<FileStat> StatAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        var node = GetNode(normalizedPath);
        if (node == null)
            throw new FileStorageNotFoundError(path);

        var stat = new FileStat
        {
            Name = node.Name,
            Type = node.Type,
            Size = node.Size,
            Created = node.CreatedAt,
            Modified = node.ModifiedAt,
            Permissions = node.Permissions,
        };

        return Task.FromResult(stat);
    }

    public Task MkdirAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath == "/")
            return Task.CompletedTask;

        if (recursive)
        {
            var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var currentPath = "";
            foreach (var part in parts)
            {
                var parentForThis = currentPath == "" ? "/" : "/" + currentPath;
                currentPath = currentPath == "" ? part : currentPath + "/" + part;
                var fullPath = "/" + currentPath;

                var existing = GetNode(fullPath);
                if (existing != null)
                {
                    if (existing.Type != "directory")
                        throw new FileStorageNotADirectoryError(part);
                    continue;
                }

                InsertDirectory(fullPath, part, parentForThis);
            }
        }
        else
        {
            var parentPath = GetParentPath(normalizedPath);
            var dirName = GetName(normalizedPath);

            if (parentPath != "/")
            {
                var parent = GetNode(parentPath);
                if (parent == null)
                    throw new FileStorageNotFoundError(path);
                if (parent.Type != "directory")
                    throw new FileStorageNotADirectoryError(parentPath);
            }

            var existing = GetNode(normalizedPath);
            if (existing != null)
                throw new FileStorageExistsError(path);

            InsertDirectory(normalizedPath, dirName, parentPath);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath == "/")
            throw new FileStoragePermissionError(path);

        var node = GetNode(normalizedPath);
        if (node == null)
            throw new FileStorageNotFoundError(path);

        if (node.Type == "directory" && !recursive)
        {
            // Check if directory has children
            using var conn = CreateConnection();
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM files WHERE parent_path = @path";
            countCmd.Parameters.AddWithValue("@path", normalizedPath);
            var count = (long)countCmd.ExecuteScalar()!;
            if (count > 0)
                throw new FileStoragePermissionError(path);
        }

        if (recursive)
        {
            // Delete all descendants (paths starting with normalizedPath + "/") and the node itself
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM files WHERE path = @path OR path LIKE @prefix";
            cmd.Parameters.AddWithValue("@path", normalizedPath);
            cmd.Parameters.AddWithValue("@prefix", normalizedPath + "/%");
            cmd.ExecuteNonQuery();
        }
        else
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM files WHERE path = @path";
            cmd.Parameters.AddWithValue("@path", normalizedPath);
            cmd.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task CopyAsync(string src, string dest, CancellationToken ct = default)
    {
        var srcPath = NormalizePath(src);
        var destPath = NormalizePath(dest);

        var srcNode = GetNode(srcPath);
        if (srcNode == null)
            throw new FileStorageNotFoundError(src);

        var destParent = GetParentPath(destPath);
        var destName = GetName(destPath);

        if (destParent != "/")
        {
            var parent = GetNode(destParent);
            if (parent == null)
                throw new FileStorageNotFoundError(dest);
            if (parent.Type != "directory")
                throw new FileStorageNotADirectoryError(destParent);
        }

        var now = DateTime.UtcNow.ToString("o");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO files (path, name, type, content, size, permissions, created_at, modified_at, parent_path)
            VALUES (@path, @name, @type, @content, @size, @permissions, @created, @modified, @parent)";
        cmd.Parameters.AddWithValue("@path", destPath);
        cmd.Parameters.AddWithValue("@name", destName);
        cmd.Parameters.AddWithValue("@type", srcNode.Type);
        cmd.Parameters.AddWithValue("@content", srcNode.Content != null ? (object)srcNode.Content : DBNull.Value);
        cmd.Parameters.AddWithValue("@size", srcNode.Size);
        cmd.Parameters.AddWithValue("@permissions", srcNode.Permissions != null ? (object)srcNode.Permissions : DBNull.Value);
        cmd.Parameters.AddWithValue("@created", now);
        cmd.Parameters.AddWithValue("@modified", now);
        cmd.Parameters.AddWithValue("@parent", destParent);
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task MoveAsync(string src, string dest, CancellationToken ct = default)
    {
        var srcPath = NormalizePath(src);
        var destPath = NormalizePath(dest);

        var srcNode = GetNode(srcPath);
        if (srcNode == null)
            throw new FileStorageNotFoundError(src);

        var destParent = GetParentPath(destPath);
        var destName = GetName(destPath);

        if (destParent != "/")
        {
            var parent = GetNode(destParent);
            if (parent == null)
                throw new FileStorageNotFoundError(dest);
            if (parent.Type != "directory")
                throw new FileStorageNotADirectoryError(destParent);
        }

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE files SET path = @newPath, name = @newName, parent_path = @newParent, modified_at = @modified
            WHERE path = @oldPath";
        cmd.Parameters.AddWithValue("@newPath", destPath);
        cmd.Parameters.AddWithValue("@newName", destName);
        cmd.Parameters.AddWithValue("@newParent", destParent);
        cmd.Parameters.AddWithValue("@modified", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@oldPath", srcPath);
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        return Task.FromResult(GetNode(normalizedPath) != null);
    }

    public Task<Stream> GetDownloadStreamAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        var node = GetNode(normalizedPath);
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

    // --- Helpers ---

    private void InsertDirectory(string fullPath, string name, string parentPath)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO files (path, name, type, size, created_at, modified_at, parent_path)
            VALUES (@path, @name, 'directory', 0, @created, @modified, @parent)";
        cmd.Parameters.AddWithValue("@path", fullPath);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@created", now);
        cmd.Parameters.AddWithValue("@modified", now);
        cmd.Parameters.AddWithValue("@parent", parentPath);
        cmd.ExecuteNonQuery();
    }

    private FileRecord? GetNode(string normalizedPath)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, type, content, size, permissions, created_at, modified_at, parent_path FROM files WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", normalizedPath);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new FileRecord
        {
            Name = reader.GetString(0),
            Type = reader.GetString(1),
            Content = reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2),
            Size = reader.GetInt64(3),
            Permissions = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = reader.GetString(5),
            ModifiedAt = reader.GetString(6),
            ParentPath = reader.IsDBNull(7) ? null : reader.GetString(7),
        };
    }

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

    private class FileRecord
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "file";
        public byte[]? Content { get; set; }
        public long Size { get; set; }
        public string? Permissions { get; set; }
        public string CreatedAt { get; set; } = "";
        public string ModifiedAt { get; set; } = "";
        public string? ParentPath { get; set; }
    }
}
