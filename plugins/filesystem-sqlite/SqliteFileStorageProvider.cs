using System.Data.Common;
using System.Text;
using Microsoft.Data.Sqlite;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Plugin.Databases;

namespace Qodalis.Cli.Plugin.FileSystem.Sqlite;

/// <summary>
/// File storage provider that persists files and directories in a SQLite database.
/// Supports both file-based and in-memory databases via shared cache mode.
/// </summary>
public class SqliteFileStorageProvider : IFileStorageProvider, IDisposable
{
    /// <inheritdoc />
    public string Name => "sqlite";

    private readonly SqliteProviderOptions _options;
    private readonly string _connectionString;
    private readonly SqliteConnection? _keepAliveConnection;

    // The table and the columns whose names changed (cli-server-dotnet#99), quoted; SQLite matches the others
    // (path, name, ...) case-insensitively
    private readonly string _t;
    private readonly string _created;
    private readonly string _modified;
    private readonly string _parent;

    /// <summary>The files table's migrations (source <c>qcli-filesystem</c>), to show them in the databases plugin.</summary>
    public ICliMigrationSource MigrationSource { get; }

    /// <summary>A new connection to the database, for the databases plugin (<c>db.UseConnection(_ =&gt; provider.CreateMigrationConnection())</c>).</summary>
    public DbConnection CreateMigrationConnection() => new SqliteConnection(_connectionString);

    /// <summary>
    /// Initializes a new instance, migrating the database schema: the first start after an upgrade renames the
    /// <c>files</c> table of 1.0.0-beta.29 and older to <c>QcliFiles</c> with its columns, keeping the files.
    /// For in-memory databases, a persistent connection is kept open to prevent data loss.
    /// </summary>
    /// <param name="options">Options specifying the database path.</param>
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
            _keepAliveConnection = new SqliteConnection(_connectionString);
            _keepAliveConnection.Open();
        }

        var table = QcliTableNames.Resolve("Files", options, "files");
        var legacy = options.LegacyTableNames && table == "files";
        _t = CliSqlSchema.QuoteIdent(CliSqlDialects.Sqlite, table);
        _created = legacy ? "created_at" : "\"CreatedAt\"";
        _modified = legacy ? "modified_at" : "\"ModifiedAt\"";
        _parent = legacy ? "parent_path" : "\"ParentPath\"";
        MigrationSource = Migrations(table, legacy);

        InitializeDatabase();
    }

    private static readonly Dictionary<string, string> LegacyColumns = new()
    {
        ["id"] = "Id", ["path"] = "Path", ["name"] = "Name", ["type"] = "Type", ["content"] = "Content", ["size"] = "Size",
        ["permissions"] = "Permissions", ["created_at"] = "CreatedAt", ["modified_at"] = "ModifiedAt", ["parent_path"] = "ParentPath",
    };

    /// <summary>The files table's schema as migrations (source <c>qcli-filesystem</c>), as on the Node and Python servers.</summary>
    private static ICliMigrationSource Migrations(string table, bool legacy)
    {
        string Col(string name) => "\"" + (legacy ? LegacyColumns.First(c => c.Value == name).Key : name) + "\"";
        var t = CliSqlSchema.QuoteIdent(CliSqlDialects.Sqlite, table);
        return new CliCodeMigrationSource("qcli-filesystem",
        [
            new CliMigration
            {
                Id = "1",
                Name = "files table",
                Up = CliMigrationStep.FromCode(async db =>
                {
                    if (!legacy) await CliSqlSchema.AdoptLegacyTableAsync(db, "files", table, LegacyColumns);
                    if (await CliSqlSchema.TableExistsAsync(db, table)) return;
                    await db.ExecuteAsync($@"CREATE TABLE {t} (
                        {Col("Id")} INTEGER PRIMARY KEY AUTOINCREMENT,
                        {Col("Path")} TEXT NOT NULL UNIQUE,
                        {Col("Name")} TEXT NOT NULL,
                        {Col("Type")} TEXT NOT NULL CHECK({Col("Type")} IN ('file', 'directory')),
                        {Col("Content")} BLOB,
                        {Col("Size")} INTEGER NOT NULL DEFAULT 0,
                        {Col("Permissions")} TEXT DEFAULT '644',
                        {Col("CreatedAt")} TEXT NOT NULL,
                        {Col("ModifiedAt")} TEXT NOT NULL,
                        {Col("ParentPath")} TEXT
                    )");
                }),
            },
            new CliMigration
            {
                Id = "2",
                Name = "index on the parent path",
                Up = CliMigrationStep.FromCode(async db =>
                {
                    if (!legacy) await db.ExecuteAsync("DROP INDEX IF EXISTS \"idx_files_parent\"");
                    var index = CliSqlSchema.QuoteIdent(CliSqlDialects.Sqlite, legacy ? "idx_files_parent" : $"IX_{table}_ParentPath");
                    await db.ExecuteAsync($"CREATE INDEX IF NOT EXISTS {index} ON {t} ({Col("ParentPath")})");
                }),
            },
        ]);
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>
    /// Disposes the keep-alive connection used for in-memory databases.
    /// </summary>
    public void Dispose()
    {
        _keepAliveConnection?.Dispose();
    }

    /// <inheritdoc />
    public Task<List<FileEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);

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
        cmd.CommandText = $"SELECT name, type, size, {_modified}, permissions FROM {_t} WHERE {_parent} = @parent";
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        return WriteFileAsync(path, Encoding.UTF8.GetBytes(content), ct);
    }

    /// <inheritdoc />
    public Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        var parentPath = GetParentPath(normalizedPath);
        var fileName = GetName(normalizedPath);

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

            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE {_t} SET content = @content, size = @size, {_modified} = @modified WHERE path = @path";
            cmd.Parameters.AddWithValue("@content", content);
            cmd.Parameters.AddWithValue("@size", content.Length);
            cmd.Parameters.AddWithValue("@modified", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@path", normalizedPath);
            cmd.ExecuteNonQuery();
        }
        else
        {
            var now = DateTime.UtcNow.ToString("o");
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO {_t} (path, name, type, content, size, {_created}, {_modified}, {_parent})
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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
            using var conn = CreateConnection();
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM {_t} WHERE {_parent} = @path";
            countCmd.Parameters.AddWithValue("@path", normalizedPath);
            var count = (long)countCmd.ExecuteScalar()!;
            if (count > 0)
                throw new FileStoragePermissionError(path);
        }

        if (recursive)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {_t} WHERE path = @path OR path LIKE @prefix";
            cmd.Parameters.AddWithValue("@path", normalizedPath);
            cmd.Parameters.AddWithValue("@prefix", normalizedPath + "/%");
            cmd.ExecuteNonQuery();
        }
        else
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {_t} WHERE path = @path";
            cmd.Parameters.AddWithValue("@path", normalizedPath);
            cmd.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
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
        cmd.CommandText = $@"
            INSERT OR REPLACE INTO {_t} (path, name, type, content, size, permissions, {_created}, {_modified}, {_parent})
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

    /// <inheritdoc />
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
        cmd.CommandText = $@"
            UPDATE {_t} SET path = @newPath, name = @newName, {_parent} = @newParent, {_modified} = @modified
            WHERE path = @oldPath";
        cmd.Parameters.AddWithValue("@newPath", destPath);
        cmd.Parameters.AddWithValue("@newName", destName);
        cmd.Parameters.AddWithValue("@newParent", destParent);
        cmd.Parameters.AddWithValue("@modified", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@oldPath", srcPath);
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        return Task.FromResult(GetNode(normalizedPath) != null);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task UploadFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        return WriteFileAsync(path, content, ct);
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void InitializeDatabase()
    {
        var isMemory = _options.DbPath == ":memory:" || _options.DbPath.Contains("mode=memory");
        if (!isMemory)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_options.DbPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        // The schema is versioned (QcliMigrations / QcliMigrationRuns next to the files table)
        var runner = new CliMigrationRunner("files", new CliMigrationDatabaseOptions
        {
            Connection = _ => CreateMigrationConnection(),
            TablePrefix = _options.TablePrefix,
        }.WithSource(MigrationSource), new NoServices());
        var run = runner.UpAsync(new CliMigrationRunOptions { Trigger = "startup", Wait = true }).GetAwaiter().GetResult();
        if (run.Status == "failed") throw new InvalidOperationException($"filesystem-sqlite schema: {run.Error}");
    }

    private void InsertDirectory(string fullPath, string name, string parentPath)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            INSERT INTO {_t} (path, name, type, size, {_created}, {_modified}, {_parent})
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
        cmd.CommandText = $"SELECT name, type, content, size, permissions, {_created}, {_modified}, {_parent} FROM {_t} WHERE path = @path";
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
