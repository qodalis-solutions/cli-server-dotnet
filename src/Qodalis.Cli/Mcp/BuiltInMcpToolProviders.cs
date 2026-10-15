using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Mcp;

/// <summary>Shared JSON helpers of the built-in providers.</summary>
internal static class McpJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static JsonObject? ToObject(object? value) =>
        value == null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), Options) as JsonObject;

    public static string Pretty(JsonNode? node) => node?.ToJsonString(Options) ?? "null";
}

/// <summary>
/// Filesystem tools (only when the filesystem API is enabled with <c>AddFileSystem()</c>): <c>fs_list</c>,
/// <c>fs_stat</c>, <c>fs_read</c> (<c>files:read</c>), <c>fs_write</c>, <c>fs_mkdir</c>, <c>fs_delete</c>
/// (<c>files:write</c>). They use the same <see cref="IFileStorageProvider"/> as <c>/api/qcli/fs</c>, so its
/// allowed-paths restrictions apply.
/// </summary>
public sealed class FileSystemMcpToolProvider : IMcpToolProvider
{
    /// <summary>Default <c>maxBytes</c> of <c>fs_read</c>.</summary>
    public const int DefaultMaxReadBytes = 262144;

    private readonly IFileStorageProvider _storage;
    private readonly bool _enabled;

    public FileSystemMcpToolProvider(IFileStorageProvider storage, bool enabled)
    {
        _storage = storage;
        _enabled = enabled;
    }

    public string? Feature => "filesystem";

    public Task<IReadOnlyList<McpToolDefinition>> GetToolsAsync(McpToolContext context, CancellationToken cancellationToken)
    {
        if (!_enabled) return Task.FromResult<IReadOnlyList<McpToolDefinition>>([]);

        var path = McpSchema.Property("string", "Path of the file or directory");
        IReadOnlyList<McpToolDefinition> tools =
        [
            new()
            {
                Name = "fs_list",
                Description = "List the entries of a directory on the server's file system",
                InputSchema = McpSchema.Object(new JsonObject { ["path"] = path.DeepClone() }, "path"),
                RequiredPermissions = [CliRoutePolicy.FilesRead],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = ListAsync,
            },
            new()
            {
                Name = "fs_stat",
                Description = "Show metadata (type, size, timestamps) of a file or directory",
                InputSchema = McpSchema.Object(new JsonObject { ["path"] = path.DeepClone() }, "path"),
                RequiredPermissions = [CliRoutePolicy.FilesRead],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = StatAsync,
            },
            new()
            {
                Name = "fs_read",
                Description = "Read a UTF-8 text file (at most maxBytes bytes)",
                InputSchema = McpSchema.Object(new JsonObject
                {
                    ["path"] = path.DeepClone(),
                    ["maxBytes"] = McpSchema.Property("integer", "Maximum number of bytes to read", DefaultMaxReadBytes),
                }, "path"),
                RequiredPermissions = [CliRoutePolicy.FilesRead],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = ReadAsync,
            },
            new()
            {
                Name = "fs_write",
                Description = "Create or overwrite a text file",
                InputSchema = McpSchema.Object(new JsonObject
                {
                    ["path"] = path.DeepClone(),
                    ["content"] = McpSchema.Property("string", "Text content"),
                }, "path", "content"),
                RequiredPermissions = [CliRoutePolicy.FilesWrite],
                CallAsync = WriteAsync,
            },
            new()
            {
                Name = "fs_mkdir",
                Description = "Create a directory (and missing parents)",
                InputSchema = McpSchema.Object(new JsonObject { ["path"] = path.DeepClone() }, "path"),
                RequiredPermissions = [CliRoutePolicy.FilesWrite],
                CallAsync = MkdirAsync,
            },
            new()
            {
                Name = "fs_delete",
                Description = "Delete a file or directory (directories with contents need recursive: true)",
                InputSchema = McpSchema.Object(new JsonObject
                {
                    ["path"] = path.DeepClone(),
                    ["recursive"] = McpSchema.Property("boolean", "Delete directories with their contents", false),
                }, "path"),
                RequiredPermissions = [CliRoutePolicy.FilesWrite],
                Annotations = McpToolAnnotations.Destructive,
                CallAsync = DeleteAsync,
            },
        ];
        return Task.FromResult(tools);
    }

    private async Task<McpToolResult> ListAsync(JsonObject args, McpToolContext context, CancellationToken ct)
    {
        var path = McpSchema.GetString(args, "path")!;
        var entries = await _storage.ListAsync(path, ct);
        var text = entries.Count == 0
            ? "(empty directory)"
            : string.Join('\n', entries.Select(e => e.Type == "directory" ? $"{e.Name}/" : $"{e.Name} ({e.Size} bytes)"));
        return McpToolResult.Ok(text, new JsonObject
        {
            ["path"] = path,
            ["entries"] = JsonSerializer.SerializeToNode(entries, McpJson.Options),
        });
    }

    private async Task<McpToolResult> StatAsync(JsonObject args, McpToolContext context, CancellationToken ct)
    {
        var stat = await _storage.StatAsync(McpSchema.GetString(args, "path")!, ct);
        var structured = McpJson.ToObject(stat)!;
        var text = string.Join('\n', structured.Where(kv => kv.Value != null).Select(kv => $"{kv.Key}: {kv.Value}"));
        return McpToolResult.Ok(text, structured);
    }

    private async Task<McpToolResult> ReadAsync(JsonObject args, McpToolContext context, CancellationToken ct)
    {
        var path = McpSchema.GetString(args, "path")!;
        var maxBytes = (int)Math.Clamp(McpSchema.GetInt64(args, "maxBytes") ?? DefaultMaxReadBytes, 1, int.MaxValue);

        var stat = await _storage.StatAsync(path, ct);
        if (stat.Type == "directory") return McpToolResult.Error($"{path} is a directory");

        await using var stream = await _storage.GetDownloadStreamAsync(path, ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var truncated = false;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            var take = (int)Math.Min(read, maxBytes - buffer.Length);
            buffer.Write(chunk, 0, take);
            if (buffer.Length >= maxBytes)
            {
                truncated = take < read || stream.ReadByte() >= 0;
                break;
            }
        }

        var bytes = buffer.ToArray();
        var total = Math.Max(stat.Size, bytes.Length);
        if (!TryDecodeText(bytes, truncated, out var text))
            return McpToolResult.Error($"binary file, {total} bytes");
        if (truncated)
            text += $"\n[truncated: showing the first {bytes.Length} of {total} bytes]";
        return McpToolResult.Ok(text);
    }

    /// <summary>Strict UTF-8 decode; no NUL bytes. A multi-byte sequence cut by truncation is dropped.</summary>
    private static bool TryDecodeText(byte[] bytes, bool truncated, out string text)
    {
        text = string.Empty;
        if (Array.IndexOf(bytes, (byte)0) >= 0) return false;
        var length = bytes.Length;
        if (truncated)
        {
            // Drop an incomplete trailing UTF-8 sequence (at most 3 continuation bytes).
            var i = length - 1;
            var continuation = 0;
            while (i >= 0 && continuation < 3 && (bytes[i] & 0xC0) == 0x80) { i--; continuation++; }
            if (i >= 0 && bytes[i] >= 0xC0)
            {
                var needed = bytes[i] >= 0xF0 ? 3 : bytes[i] >= 0xE0 ? 2 : 1;
                if (continuation < needed) length = i;
            }
        }
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes, 0, length);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private async Task<McpToolResult> WriteAsync(JsonObject args, McpToolContext context, CancellationToken ct)
    {
        var path = McpSchema.GetString(args, "path")!;
        var content = McpSchema.GetString(args, "content")!;
        await _storage.WriteFileAsync(path, content, ct);
        return McpToolResult.Ok($"Wrote {Encoding.UTF8.GetByteCount(content)} bytes to {path}");
    }

    private async Task<McpToolResult> MkdirAsync(JsonObject args, McpToolContext context, CancellationToken ct)
    {
        var path = McpSchema.GetString(args, "path")!;
        if (await _storage.ExistsAsync(path, ct)) return McpToolResult.Ok($"Directory already exists: {path}");
        await _storage.MkdirAsync(path, recursive: true, ct);
        return McpToolResult.Ok($"Created directory {path}");
    }

    private async Task<McpToolResult> DeleteAsync(JsonObject args, McpToolContext context, CancellationToken ct)
    {
        var path = McpSchema.GetString(args, "path")!;
        var recursive = McpSchema.GetBoolean(args, "recursive") ?? false;
        var stat = await _storage.StatAsync(path, ct);
        await _storage.RemoveAsync(path, recursive, ct);
        return McpToolResult.Ok($"Deleted {stat.Type} {path}");
    }
}

/// <summary>
/// Data explorer tools (only when at least one data source is registered): <c>data_sources</c>,
/// <c>data_schema</c> and <c>data_query</c> (<c>data:query</c>). Queries run through the same executor
/// (timeouts, row limits) as <c>POST /api/qcli/data-explorer/execute</c>.
/// </summary>
public sealed class DataExplorerMcpToolProvider : IMcpToolProvider
{
    private readonly IDataExplorerRegistry _registry;
    private readonly IDataExplorerExecutorService _executor;

    public DataExplorerMcpToolProvider(IDataExplorerRegistry registry, IDataExplorerExecutorService executor)
    {
        _registry = registry;
        _executor = executor;
    }

    public string? Feature => "data-explorer";

    public Task<IReadOnlyList<McpToolDefinition>> GetToolsAsync(McpToolContext context, CancellationToken cancellationToken)
    {
        if (_registry.GetSources().Count == 0) return Task.FromResult<IReadOnlyList<McpToolDefinition>>([]);

        var source = McpSchema.Property("string", "Name of the data source (see data_sources)");
        IReadOnlyList<McpToolDefinition> tools =
        [
            new()
            {
                Name = "data_sources",
                Description = "List the data explorer sources (databases) and their query languages",
                InputSchema = McpSchema.Object(),
                RequiredPermissions = [CliRoutePolicy.DataQuery],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = SourcesAsync,
            },
            new()
            {
                Name = "data_schema",
                Description = "Show the schema (tables/collections and columns) of a data source",
                InputSchema = McpSchema.Object(new JsonObject { ["source"] = source.DeepClone() }, "source"),
                RequiredPermissions = [CliRoutePolicy.DataQuery],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = SchemaAsync,
            },
            new()
            {
                Name = "data_query",
                Description = "Run a query against a data source (in the source's language, e.g. SQL)",
                InputSchema = McpSchema.Object(new JsonObject
                {
                    ["source"] = source.DeepClone(),
                    ["query"] = McpSchema.Property("string", "The query"),
                    ["parameters"] = McpSchema.Property("object", "Optional query parameters"),
                }, "source", "query"),
                RequiredPermissions = [CliRoutePolicy.DataQuery],
                CallAsync = QueryAsync,
            },
        ];
        return Task.FromResult(tools);
    }

    private Task<McpToolResult> SourcesAsync(JsonObject args, McpToolContext context, CancellationToken ct)
    {
        var sources = _registry.GetSources();
        var text = string.Join('\n', sources.Select(s =>
            $"{s.Name} ({JsonNamingPolicy.CamelCase.ConvertName(s.Language.ToString())}): {s.Description}"));
        return Task.FromResult(McpToolResult.Ok(text, new JsonObject
        {
            ["sources"] = JsonSerializer.SerializeToNode(sources, McpJson.Options),
        }));
    }

    private async Task<McpToolResult> SchemaAsync(JsonObject args, McpToolContext context, CancellationToken ct)
    {
        var name = McpSchema.GetString(args, "source")!;
        var entry = _registry.Get(name);
        if (entry == null) return McpToolResult.Error($"Unknown data source: '{name}'");

        var (provider, options) = entry.Value;
        var schema = await provider.GetSchemaAsync(options, ct);
        if (schema == null) return McpToolResult.Error("Schema introspection is not supported by this data source.");

        var text = string.Join('\n', schema.Tables.Select(t =>
            $"{t.Name} ({t.Type}): {string.Join(", ", t.Columns.Select(c => $"{c.Name} {c.Type}{(c.PrimaryKey ? " PK" : "")}"))}"));
        return McpToolResult.Ok(text.Length == 0 ? "(no tables)" : text, McpJson.ToObject(schema));
    }

    private async Task<McpToolResult> QueryAsync(JsonObject args, McpToolContext context, CancellationToken ct)
    {
        Dictionary<string, object>? parameters = null;
        if (args["parameters"] is JsonObject p)
        {
            parameters = p.Where(kv => kv.Value != null)
                .ToDictionary(kv => kv.Key, kv => (object)JsonSerializer.SerializeToElement(kv.Value));
        }

        var result = await _executor.ExecuteAsync(new DataExplorerExecuteRequest
        {
            Source = McpSchema.GetString(args, "source")!,
            Query = McpSchema.GetString(args, "query")!,
            Parameters = parameters,
        }, ct);

        if (!result.Success) return McpToolResult.Error(result.Error ?? "Query failed");

        var structured = McpJson.ToObject(result)!;
        return McpToolResult.Ok(RenderRows(result, structured), structured);
    }

    /// <summary>Columns + rows separated by <c> | </c> (rows as arrays or objects), then the row count.</summary>
    private static string RenderRows(DataExplorerResult result, JsonObject structured)
    {
        var lines = new List<string>();
        var columns = result.Columns ?? [];
        if (columns.Count > 0) lines.Add(string.Join(" | ", columns));
        foreach (var row in structured["rows"] as JsonArray ?? [])
        {
            lines.Add(row switch
            {
                JsonArray cells => string.Join(" | ", cells.Select(Cell)),
                JsonObject obj when columns.Count > 0 => string.Join(" | ", columns.Select(c => Cell(obj[c]))),
                JsonObject obj => obj.ToJsonString(),
                _ => Cell(row),
            });
        }
        lines.Add($"({result.RowCount} row{(result.RowCount == 1 ? "" : "s")}{(result.Truncated ? ", truncated" : "")})");
        return string.Join('\n', lines);
    }

    private static string Cell(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => node.ToJsonString(),
    };
}
