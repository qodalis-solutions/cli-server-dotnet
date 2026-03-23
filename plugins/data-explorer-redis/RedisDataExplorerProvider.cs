using Qodalis.Cli.Abstractions.DataExplorer;
using StackExchange.Redis;

namespace Qodalis.Cli.Plugin.DataExplorer.Redis;

/// <summary>
/// Data explorer provider for Redis using StackExchange.Redis.
/// Supports a curated set of safe Redis commands and normalizes results into tabular format.
/// </summary>
public class RedisDataExplorerProvider : IDataExplorerProvider
{
    private readonly string _connectionString;

    /// <summary>
    /// The set of Redis commands that are allowed for execution through this provider.
    /// </summary>
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "SET", "DEL", "KEYS", "EXISTS", "TYPE", "TTL", "PTTL", "EXPIRE",
        "HGET", "HSET", "HGETALL", "HMGET", "HMSET", "HDEL", "HKEYS", "HVALS", "HLEN",
        "LRANGE", "LLEN", "LINDEX", "LPUSH", "RPUSH", "LPOP", "RPOP",
        "SMEMBERS", "SCARD", "SISMEMBER", "SADD", "SREM",
        "ZADD", "ZRANGE", "ZRANGEBYSCORE", "ZCARD", "ZSCORE", "ZREM",
        "MGET", "MSET", "APPEND", "STRLEN", "INCR", "DECR", "INCRBY", "DECRBY",
        "SCAN", "HSCAN", "SSCAN", "ZSCAN", "INFO", "DBSIZE", "SELECT", "PING"
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisDataExplorerProvider"/> class.
    /// </summary>
    /// <param name="connectionString">The Redis connection string.</param>
    public RedisDataExplorerProvider(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Executes a Redis command and returns the results in a normalized tabular format.
    /// Only commands in the allowed set are permitted.
    /// </summary>
    /// <param name="context">The execution context containing the Redis command string and options.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The query result containing columns, rows, and metadata.</returns>
    public async Task<DataExplorerResult> ExecuteAsync(
        DataExplorerExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var result = new DataExplorerResult
        {
            Success = true,
            Source = context.Options.Name,
            Language = context.Options.Language,
            DefaultOutputFormat = context.Options.DefaultOutputFormat
        };

        ConnectionMultiplexer? multiplexer = null;
        try
        {
            multiplexer = await ConnectionMultiplexer.ConnectAsync(_connectionString);
            var db = multiplexer.GetDatabase();

            var parts = ParseCommand(context.Query);
            if (parts.Length == 0)
            {
                result.Success = false;
                result.Error = "Empty command.";
                return result;
            }

            var command = parts[0];
            if (!AllowedCommands.Contains(command))
            {
                result.Success = false;
                result.Error = $"Command '{command.ToUpperInvariant()}' is not allowed.";
                return result;
            }

            var args = parts.Skip(1).Select(a => (object)a).ToArray();
            var redisResult = await db.ExecuteAsync(command, args);

            NormalizeResult(command, redisResult, result);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }
        finally
        {
            multiplexer?.Dispose();
        }

        return result;
    }

    /// <summary>
    /// Retrieves the Redis key space schema by scanning keys and grouping them by data type.
    /// Samples up to 1000 keys to build the schema.
    /// </summary>
    /// <param name="options">The provider options containing the data source name.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The schema result with tables representing Redis data types and their typical structure.</returns>
    public async Task<DataExplorerSchemaResult?> GetSchemaAsync(
        DataExplorerProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        ConnectionMultiplexer? multiplexer = null;
        try
        {
            multiplexer = await ConnectionMultiplexer.ConnectAsync(_connectionString);
            var db = multiplexer.GetDatabase();
            var server = multiplexer.GetServers().FirstOrDefault();
            if (server == null)
            {
                return new DataExplorerSchemaResult { Source = options.Name };
            }

            var keysByType = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var count = 0;

            await foreach (var key in server.KeysAsync(pattern: "*", pageSize: 100))
            {
                if (count >= 1000) break;
                count++;

                var keyStr = key.ToString();
                var keyType = await db.KeyTypeAsync(key);
                var typeName = keyType.ToString();

                if (!keysByType.ContainsKey(typeName))
                    keysByType[typeName] = new List<string>();

                keysByType[typeName].Add(keyStr);
            }

            var tables = new List<DataExplorerSchemaTable>();

            foreach (var (typeName, keys) in keysByType)
            {
                var columns = GetColumnsForType(typeName);
                tables.Add(new DataExplorerSchemaTable
                {
                    Name = typeName,
                    Type = typeName,
                    Columns = columns
                });
            }

            return new DataExplorerSchemaResult
            {
                Source = options.Name,
                Tables = tables
            };
        }
        finally
        {
            multiplexer?.Dispose();
        }
    }

    /// <summary>
    /// Parses a Redis command string into individual tokens, respecting quoted strings.
    /// </summary>
    private static string[] ParseCommand(string query)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuote = false;
        var quoteChar = '"';

        foreach (var ch in query.Trim())
        {
            if (inQuote)
            {
                if (ch == quoteChar)
                    inQuote = false;
                else
                    current.Append(ch);
            }
            else if (ch == '"' || ch == '\'')
            {
                inQuote = true;
                quoteChar = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            parts.Add(current.ToString());

        return parts.ToArray();
    }

    /// <summary>
    /// Normalizes a raw Redis result into the tabular format expected by <see cref="DataExplorerResult"/>,
    /// applying command-specific formatting for commands like HGETALL, KEYS, INFO, etc.
    /// </summary>
    private static void NormalizeResult(string command, RedisResult redisResult, DataExplorerResult result)
    {
        var upperCommand = command.ToUpperInvariant();

        switch (upperCommand)
        {
            case "HGETALL":
            {
                var values = (RedisValue[])redisResult!;
                result.Columns = new List<string> { "Field", "Value" };
                var rows = new List<object>();
                for (var i = 0; i + 1 < values.Length; i += 2)
                    rows.Add(new object[] { values[i].ToString(), values[i + 1].ToString() });
                result.Rows = rows;
                result.RowCount = rows.Count;
                break;
            }
            case "KEYS":
            case "SMEMBERS":
            case "LRANGE":
            case "HKEYS":
            case "HVALS":
            {
                var values = (RedisValue[])redisResult!;
                result.Columns = new List<string> { "Value" };
                var rows = values.Select(v => (object)new object[] { v.ToString() }).ToList();
                result.Rows = rows;
                result.RowCount = rows.Count;
                break;
            }
            case "MGET":
            case "HMGET":
            {
                var values = (RedisValue[])redisResult!;
                result.Columns = new List<string> { "Value" };
                var rows = values.Select(v => (object)new object[] { v.IsNull ? "(nil)" : v.ToString() }).ToList();
                result.Rows = rows;
                result.RowCount = rows.Count;
                break;
            }
            case "ZRANGE":
            case "ZRANGEBYSCORE":
            {
                var values = (RedisValue[])redisResult!;
                result.Columns = new List<string> { "Member" };
                var rows = values.Select(v => (object)new object[] { v.ToString() }).ToList();
                result.Rows = rows;
                result.RowCount = rows.Count;
                break;
            }
            case "INFO":
            {
                var info = redisResult.ToString() ?? string.Empty;
                result.Columns = new List<string> { "Section", "Key", "Value" };
                var rows = new List<object>();
                var section = "server";
                foreach (var line in info.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    if (trimmed.StartsWith('#'))
                    {
                        section = trimmed.TrimStart('#').Trim();
                        continue;
                    }
                    var colonIdx = trimmed.IndexOf(':');
                    if (colonIdx < 0) continue;
                    var key = trimmed[..colonIdx].Trim();
                    var val = trimmed[(colonIdx + 1)..].Trim();
                    rows.Add(new object[] { section, key, val });
                }
                result.Rows = rows;
                result.RowCount = rows.Count;
                break;
            }
            default:
            {
                var value = redisResult.ToString() ?? "(nil)";
                result.Columns = new List<string> { "Result" };
                result.Rows = new List<object> { new object[] { value } };
                result.RowCount = 1;
                break;
            }
        }
    }

    /// <summary>
    /// Returns the typical column definitions for a given Redis data type (e.g., string, hash, list, set, zset).
    /// </summary>
    private static List<DataExplorerSchemaColumn> GetColumnsForType(string typeName)
    {
        return typeName.ToLowerInvariant() switch
        {
            "string" => new List<DataExplorerSchemaColumn>
            {
                new() { Name = "key", Type = "string", Nullable = false, PrimaryKey = true },
                new() { Name = "value", Type = "string", Nullable = true, PrimaryKey = false },
                new() { Name = "ttl", Type = "integer", Nullable = true, PrimaryKey = false }
            },
            "hash" => new List<DataExplorerSchemaColumn>
            {
                new() { Name = "key", Type = "string", Nullable = false, PrimaryKey = true },
                new() { Name = "field", Type = "string", Nullable = false, PrimaryKey = false },
                new() { Name = "value", Type = "string", Nullable = true, PrimaryKey = false }
            },
            "list" => new List<DataExplorerSchemaColumn>
            {
                new() { Name = "key", Type = "string", Nullable = false, PrimaryKey = true },
                new() { Name = "index", Type = "integer", Nullable = false, PrimaryKey = false },
                new() { Name = "value", Type = "string", Nullable = true, PrimaryKey = false }
            },
            "set" => new List<DataExplorerSchemaColumn>
            {
                new() { Name = "key", Type = "string", Nullable = false, PrimaryKey = true },
                new() { Name = "member", Type = "string", Nullable = false, PrimaryKey = false }
            },
            "zset" => new List<DataExplorerSchemaColumn>
            {
                new() { Name = "key", Type = "string", Nullable = false, PrimaryKey = true },
                new() { Name = "member", Type = "string", Nullable = false, PrimaryKey = false },
                new() { Name = "score", Type = "float", Nullable = false, PrimaryKey = false }
            },
            _ => new List<DataExplorerSchemaColumn>
            {
                new() { Name = "key", Type = "string", Nullable = false, PrimaryKey = true },
                new() { Name = "value", Type = "string", Nullable = true, PrimaryKey = false }
            }
        };
    }
}
