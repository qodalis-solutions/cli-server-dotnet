using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Qodalis.Cli.Abstractions.DataExplorer;

namespace Qodalis.Cli.Services;

/// <summary>
/// The source options that keep data from leaking: permissions, read-only rules by query language, allowed
/// objects, column masking and dropping (spec: web-cli
/// <c>docs/superpowers/specs/2026-11-12-data-explorer-hardening-design.md</c>). The same rules as the Node and
/// Python servers.
/// </summary>
public static partial class DataExplorerPolicy
{
    /// <summary>The value that replaces a masked column.</summary>
    public const string Mask = "***";

    private static readonly ConcurrentDictionary<string, Regex> GlobCache = new();

    /// <summary>A glob (<c>*</c> matches anything) as a case-insensitive regular expression.</summary>
    public static Regex GlobToRegex(string pattern) =>
        GlobCache.GetOrAdd(pattern, p =>
            new Regex("^" + Regex.Escape(p).Replace("\\*", ".*") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline));

    /// <summary>Whether <paramref name="name"/> matches one of the glob <paramref name="patterns"/>.</summary>
    public static bool MatchesAny(string name, IReadOnlyCollection<string>? patterns) =>
        patterns != null && patterns.Any(p => GlobToRegex(p).IsMatch(name));

    /// <summary>The permissions of <paramref name="permissions"/> the caller lacks (none on an open server).</summary>
    public static List<string> MissingPermissions(IReadOnlyCollection<string>? permissions, DataExplorerCaller? caller)
    {
        if (permissions == null || permissions.Count == 0 || caller?.HasPermission == null) return [];
        return permissions.Where(p => !caller.HasPermission(p)).ToList();
    }

    /// <summary>Whether the caller may use the source.</summary>
    public static bool CanUseSource(DataExplorerProviderOptions options, DataExplorerCaller? caller) =>
        MissingPermissions(options.RequiredPermissions, caller).Count == 0;

    /// <summary>Whether the caller may export results of the source (advisory).</summary>
    public static bool CanExport(DataExplorerProviderOptions options, DataExplorerCaller? caller) =>
        MissingPermissions(options.ExportPermissions, caller).Count == 0;

    // ── SQL tokens ──────────────────────────────────────────────────────

    internal enum SqlTokenKind { Word, Ident, Punct }

    /// <summary>A SQL token: <see cref="Value"/> is upper-case for words; <see cref="Raw"/> is as written.</summary>
    internal readonly record struct SqlToken(SqlTokenKind Kind, string Value, string Raw);

    [GeneratedRegex(@"\G\$[A-Za-z_]*\$")]
    private static partial Regex DollarTag();

    /// <summary>
    /// Splits SQL into words, quoted identifiers and punctuation; comments and string literals are dropped, so
    /// keywords inside them do not count. <paramref name="dialectQuotes"/> also reads backslash escapes in string
    /// literals and Postgres dollar quotes (<c>$tag$ ... $tag$</c>); dialects without them end the literal earlier,
    /// so the checks read the query both ways.
    /// </summary>
    internal static List<SqlToken> TokenizeSql(string sql, bool dialectQuotes = true)
    {
        var tokens = new List<SqlToken>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (c == '-' && next == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            // MySQL comment ('# ...'); '#name' is a SQL Server temporary table
            if (c == '#' && (i + 1 >= sql.Length || char.IsWhiteSpace(next)))
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && next == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }
            if (c == '\'')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'') i += 2;
                    else if (dialectQuotes && sql[i] == '\\') i += 2;
                    else if (sql[i] == '\'') break;
                    else i++;
                }
                i++;
                continue;
            }
            if (c == '$' && dialectQuotes)
            {
                var m = DollarTag().Match(sql, i);
                if (m.Success)
                {
                    var end = sql.IndexOf(m.Value, i + m.Length, StringComparison.Ordinal);
                    i = end < 0 ? sql.Length : end + m.Length;
                    continue;
                }
            }
            if (c is '"' or '`' or '[')
            {
                var close = c == '[' ? ']' : c;
                var end = sql.IndexOf(close, i + 1);
                var name = sql[(i + 1)..(end < 0 ? sql.Length : end)];
                tokens.Add(new SqlToken(SqlTokenKind.Ident, name, name));
                i = end < 0 ? sql.Length : end + 1;
                continue;
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                var j = i + 1;
                while (j < sql.Length && (char.IsAsciiLetterOrDigit(sql[j]) || sql[j] == '_' || sql[j] == '$')) j++;
                var raw = sql[i..j];
                tokens.Add(new SqlToken(SqlTokenKind.Word, raw.ToUpperInvariant(), raw));
                i = j;
                continue;
            }
            if (char.IsWhiteSpace(c) || char.IsAsciiDigit(c))
            {
                i++;
                continue;
            }
            tokens.Add(new SqlToken(SqlTokenKind.Punct, c.ToString(), c.ToString()));
            i++;
        }
        return tokens;
    }

    private static readonly HashSet<string> SqlReadStarts = ["SELECT", "WITH", "EXPLAIN", "SHOW", "DESCRIBE", "DESC", "VALUES", "TABLE"];

    private static readonly HashSet<string> SqlWriteWords =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "UPSERT", "REPLACE", "CREATE", "ALTER", "DROP", "TRUNCATE", "GRANT",
        "REVOKE", "COPY", "CALL", "EXEC", "EXECUTE", "INTO", "LOCK", "VACUUM", "ATTACH", "DETACH", "PRAGMA", "SET",
    ];

    /// <summary>Write keywords that are also read-only functions when a <c>(</c> follows (<c>REPLACE(name, 'a', 'b')</c>).</summary>
    private static readonly HashSet<string> SqlFunctionWords = ["REPLACE", "INSERT"];

    private static string? SqlReadOnlyError(string query) =>
        SqlReadOnlyError(TokenizeSql(query, dialectQuotes: true)) ?? SqlReadOnlyError(TokenizeSql(query, dialectQuotes: false));

    private static string? SqlReadOnlyError(List<SqlToken> tokens)
    {
        var statements = new List<List<SqlToken>> { new() };
        foreach (var t in tokens)
        {
            if (t.Kind == SqlTokenKind.Punct && t.Value == ";") statements.Add([]);
            else statements[^1].Add(t);
        }
        var nonEmpty = statements.Where(s => s.Count > 0).ToList();
        if (nonEmpty.Count != 1) return "Read-only source: run one statement at a time";
        var statement = nonEmpty[0];
        var first = statement[0];
        if (first.Kind != SqlTokenKind.Word || !SqlReadStarts.Contains(first.Value))
        {
            return "Read-only source: only SELECT, WITH, EXPLAIN, SHOW, DESCRIBE, VALUES or TABLE queries are allowed";
        }
        for (var i = 0; i < statement.Count; i++)
        {
            var t = statement[i];
            if (t.Kind != SqlTokenKind.Word || !SqlWriteWords.Contains(t.Value)) continue;
            if (SqlFunctionWords.Contains(t.Value) && i + 1 < statement.Count && statement[i + 1].Value == "(") continue;
            return $"Read-only source: {t.Value} is not allowed";
        }
        return null;
    }

    /// <summary>Functions whose arguments use FROM without naming a table (<c>EXTRACT(YEAR FROM day)</c>).</summary>
    private static readonly HashSet<string> SqlFromFunctions = ["EXTRACT", "SUBSTRING", "SUBSTR", "TRIM", "POSITION", "OVERLAY"];

    private static readonly HashSet<string> SqlObjectKeywords = ["FROM", "JOIN", "INTO", "UPDATE", "TABLE"];

    private static readonly HashSet<string> SqlClauseWords =
    [
        "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "ON", "USING", "GROUP", "ORDER", "HAVING",
        "LIMIT", "OFFSET", "UNION", "EXCEPT", "INTERSECT", "WINDOW", "FETCH", "FOR", "NATURAL", "SET", "VALUES",
        "SELECT", "RETURNING", "TABLESAMPLE", "WITH",
    ];

    /// <summary>
    /// The tables a SQL query names (after FROM, JOIN, INTO, UPDATE, TABLE), without the CTE names it defines.
    /// Reads the query both with and without dialect quotes and returns every name either way finds.
    /// </summary>
    public static List<string> SqlObjects(string query)
    {
        var names = SqlObjects(TokenizeSql(query, dialectQuotes: true));
        foreach (var name in SqlObjects(TokenizeSql(query, dialectQuotes: false)))
            if (!names.Contains(name)) names.Add(name);
        return names;
    }

    private static List<string> SqlObjects(List<SqlToken> tokens)
    {
        SqlToken? At(int index) => index >= 0 && index < tokens.Count ? tokens[index] : null;
        static bool IsName(SqlToken? t) => t is { Kind: SqlTokenKind.Ident or SqlTokenKind.Word };

        // CTE names: name [ (columns) ] AS (
        var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!IsName(tokens[i])) continue;
            var j = i + 1;
            if (At(j)?.Value == "(")
            {
                var depth = 0;
                for (; j < tokens.Count; j++)
                {
                    if (tokens[j].Value == "(") depth++;
                    else if (tokens[j].Value == ")" && --depth == 0) break;
                }
                j++;
            }
            if (At(j)?.Value == "AS" && At(j + 1)?.Value == "(") cteNames.Add(tokens[i].Raw);
        }

        (string Name, int Next)? ReadName(int start)
        {
            if (!IsName(At(start))) return null;
            var parts = new List<string> { tokens[start].Raw };
            var k = start + 1;
            while (At(k)?.Value == "." && IsName(At(k + 1)))
            {
                parts.Add(tokens[k + 1].Raw);
                k += 2;
            }
            return (string.Join('.', parts), k);
        }

        var objects = new List<string>();
        var openers = new Stack<string?>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Value == "(")
            {
                var previous = At(i - 1);
                openers.Push(previous is { Kind: SqlTokenKind.Word } ? previous.Value.Value : null);
                continue;
            }
            if (t.Value == ")")
            {
                openers.TryPop(out _);
                continue;
            }
            if (t.Kind != SqlTokenKind.Word || !SqlObjectKeywords.Contains(t.Value)) continue;
            if (t.Value == "FROM" && openers.TryPeek(out var opener) && opener != null && SqlFromFunctions.Contains(opener)) continue;
            var k = i + 1;
            // FROM a, b, c
            while (true)
            {
                if (At(k)?.Value is "ONLY" or "LATERAL") k++;
                var name = ReadName(k);
                if (name == null) break;
                if (!cteNames.Contains(name.Value.Name)) objects.Add(name.Value.Name);
                k = name.Value.Next;
                // An alias: [AS] alias
                if (At(k)?.Value == "AS") k += 2;
                else if (At(k) is { Kind: SqlTokenKind.Word } alias && !SqlClauseWords.Contains(alias.Value)) k++;
                if (At(k)?.Value != "," || t.Value != "FROM") break;
                k++;
            }
        }
        return objects;
    }

    // ── MongoDB ─────────────────────────────────────────────────────────

    private static readonly HashSet<string> MongoReadMethods =
        new(["find", "findone", "countdocuments", "estimateddocumentcount", "distinct", "aggregate"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^show\s+(collections|dbs|databases)\s*;?$", RegexOptions.IgnoreCase)]
    private static partial Regex MongoShow();

    [GeneratedRegex(@"^db\.(?:getCollection\(\s*['""]([^'""]+)['""]\s*\)|([A-Za-z0-9_$-]+(?:\.[A-Za-z0-9_$-]+)*?))\.(\w+)\s*\(")]
    private static partial Regex MongoCall();

    [GeneratedRegex(@"[""']?\$(out|merge)[""']?\s*:")]
    private static partial Regex MongoWriteStage();

    [GeneratedRegex(@"^show\s+collections", RegexOptions.IgnoreCase)]
    private static partial Regex MongoShowCollections();

    private static string? MongoReadOnlyError(string query)
    {
        var q = query.Trim();
        if (MongoShow().IsMatch(q)) return null;
        var m = MongoCall().Match(q);
        if (!m.Success || !MongoReadMethods.Contains(m.Groups[3].Value))
        {
            return "Read-only source: only find, findOne, countDocuments, estimatedDocumentCount, distinct and aggregate are allowed";
        }
        if (m.Groups[3].Value.Equals("aggregate", StringComparison.OrdinalIgnoreCase) && MongoWriteStage().IsMatch(q))
        {
            return "Read-only source: $out and $merge are not allowed";
        }
        return null;
    }

    private static List<string>? MongoObjects(string query)
    {
        var q = query.Trim();
        if (MongoShow().IsMatch(q)) return [];
        var m = MongoCall().Match(q);
        if (!m.Success) return null;
        return [m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value];
    }

    // ── Redis ───────────────────────────────────────────────────────────

    private static readonly HashSet<string> RedisRead =
    [
        "GET", "MGET", "STRLEN", "GETRANGE", "HGET", "HMGET", "HGETALL", "HKEYS", "HVALS", "HLEN", "HEXISTS", "LRANGE",
        "LLEN", "LINDEX", "SMEMBERS", "SCARD", "SISMEMBER", "ZRANGE", "ZRANGEBYSCORE", "ZREVRANGE", "ZCARD", "ZSCORE",
        "TTL", "PTTL", "EXISTS", "TYPE", "KEYS", "SCAN", "INFO", "DBSIZE", "PING",
    ];

    private static readonly HashSet<string> RedisNoKeys = ["INFO", "DBSIZE", "PING"];

    /// <summary>Commands whose every argument is a key.</summary>
    private static readonly HashSet<string> RedisAllKeys = ["MGET", "EXISTS", "DEL", "UNLINK", "TOUCH"];

    [GeneratedRegex(@"""[^""]*""|'[^']*'|\S+")]
    private static partial Regex RedisWord();

    private static List<string> RedisWords(string query) =>
        RedisWord().Matches(query.Trim()).Select(m => m.Value.Trim('"', '\'')).ToList();

    private static string? RedisReadOnlyError(string query)
    {
        var command = RedisWords(query).FirstOrDefault()?.ToUpperInvariant();
        return command != null && RedisRead.Contains(command) ? null : $"Read-only source: {command ?? "this command"} is not allowed";
    }

    /// <summary>The keys (or key patterns) a Redis command names; null when it may touch any key.</summary>
    private static List<string>? RedisObjects(string query)
    {
        var words = RedisWords(query);
        if (words.Count == 0) return [];
        var cmd = words[0].ToUpperInvariant();
        var args = words.Skip(1).ToList();
        if (RedisNoKeys.Contains(cmd)) return [];
        if (cmd == "SCAN")
        {
            var match = args.FindIndex(a => a.Equals("MATCH", StringComparison.OrdinalIgnoreCase));
            return match >= 0 && match + 1 < args.Count ? [args[match + 1]] : null;
        }
        if (cmd == "KEYS") return args.Count > 0 ? [args[0]] : null;
        if (RedisAllKeys.Contains(cmd)) return args;
        if (cmd == "MSET") return args.Where((_, i) => i % 2 == 0).ToList();
        return args.Count > 0 ? [args[0]] : [];
    }

    // ── Elasticsearch ───────────────────────────────────────────────────

    [GeneratedRegex(@"/(_search|_count|_msearch|_mget|_field_caps|_validate/query)$")]
    private static partial Regex EsReadPost();

    [GeneratedRegex(@"^(GET|POST|PUT|DELETE|HEAD|PATCH)\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex EsRequest();

    private static (string Verb, string Path) EsRequestLine(string query)
    {
        var first = query.Trim().Split('\n')[0].Trim();
        var m = EsRequest().Match(first);
        if (m.Success)
        {
            var path = m.Groups[2].Value;
            return (m.Groups[1].Value.ToUpperInvariant(), path.StartsWith('/') ? path : "/" + path);
        }
        return ("GET", first.StartsWith('/') ? first : "/" + first);
    }

    private static string? EsReadOnlyError(string query)
    {
        var (verb, path) = EsRequestLine(query);
        if (verb is "GET" or "HEAD") return null;
        var bare = path.Split('?')[0];
        if (verb == "POST" && EsReadPost().IsMatch(bare)) return null;
        return $"Read-only source: {verb} {bare} is not allowed";
    }

    private static List<string>? EsObjects(string query)
    {
        var (_, path) = EsRequestLine(query);
        var first = path.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(first) || first.StartsWith('_')) return null;
        return first.Split(',').ToList();
    }

    // ── Checks ──────────────────────────────────────────────────────────

    /// <summary>Why a read-only source refuses the query, or null when it only reads.</summary>
    public static string? ReadOnlyError(DataExplorerLanguage language, string query) => language switch
    {
        DataExplorerLanguage.Sql => SqlReadOnlyError(query),
        // MongoDB sources use either language
        DataExplorerLanguage.Shell or DataExplorerLanguage.Json => MongoReadOnlyError(query),
        DataExplorerLanguage.Redis => RedisReadOnlyError(query),
        DataExplorerLanguage.Elasticsearch => EsReadOnlyError(query),
        _ => $"Read-only source: {LanguageName(language)} queries cannot be checked; set readOnly: false to allow them",
    };

    /// <summary>The objects a query names, by language; null when they cannot be told (then refused under <c>AllowedObjects</c>).</summary>
    public static List<string>? QueryObjects(DataExplorerLanguage language, string query) => language switch
    {
        DataExplorerLanguage.Sql => SqlObjects(query),
        DataExplorerLanguage.Shell or DataExplorerLanguage.Json => MongoObjects(query),
        DataExplorerLanguage.Redis => RedisObjects(query),
        DataExplorerLanguage.Elasticsearch => EsObjects(query),
        _ => null,
    };

    /// <summary>
    /// Whether an object name is allowed: a pattern with a dot matches the qualified name, one without matches the
    /// last part (a table in any schema). Redis key patterns and Elasticsearch index patterns in the query are
    /// matched as written (<c>rates:*</c> is allowed by <c>rates:*</c>, <c>*</c> is not).
    /// </summary>
    public static bool IsObjectAllowed(string name, IReadOnlyCollection<string> patterns)
    {
        var last = name.Split('.')[^1];
        return patterns.Any(p => GlobToRegex(p).IsMatch(p.Contains('.') ? name : last) || GlobToRegex(p).IsMatch(name));
    }

    /// <summary>Why the query is refused under <c>AllowedObjects</c>, or null.</summary>
    public static string? ObjectsError(DataExplorerProviderOptions options, string query)
    {
        var patterns = options.AllowedObjects;
        if (patterns == null || patterns.Count == 0) return null;
        var objects = QueryObjects(options.Language, query);
        if (objects == null) return "This source only accepts queries that name the objects they read";
        var denied = objects.FirstOrDefault(o => !IsObjectAllowed(o, patterns));
        return denied != null ? $"'{denied}' is not available on this source" : null;
    }

    private static string LanguageName(DataExplorerLanguage language) =>
        System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(language.ToString());

    // ── Results ─────────────────────────────────────────────────────────

    /// <summary>Masks and drops fields of a nested value (documents, arrays); other values are returned as they are.</summary>
    private static object? MaskValue(object? value, List<string> mask, List<string> drop)
    {
        switch (value)
        {
            case null or string or byte[]:
                return value;
            case JsonObject obj:
            {
                var output = new JsonObject();
                foreach (var (key, v) in obj)
                {
                    if (MatchesAny(key, drop)) continue;
                    output[key] = MatchesAny(key, mask)
                        ? (v == null ? null : JsonValue.Create(Mask))
                        : MaskValue(v?.DeepClone(), mask, drop) as JsonNode;
                }
                return output;
            }
            case JsonArray array:
                return new JsonArray(array.Select(v => MaskValue(v?.DeepClone(), mask, drop) as JsonNode).ToArray());
            case IDictionary dictionary:
            {
                var output = new Dictionary<string, object?>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = entry.Key.ToString() ?? string.Empty;
                    if (MatchesAny(key, drop)) continue;
                    output[key] = MatchesAny(key, mask)
                        ? (entry.Value == null ? null : Mask)
                        : MaskValue(entry.Value, mask, drop);
                }
                return output;
            }
            case IList list:
                return list.Cast<object?>().Select(v => MaskValue(v, mask, drop)).ToList();
            default:
                return value;
        }
    }

    /// <summary>Masks and drops columns (<c>MaskColumns</c>, <c>DropColumns</c>) in a result, in place; returns it.</summary>
    public static DataExplorerResult ApplyColumnPolicy(DataExplorerResult result, DataExplorerProviderOptions options)
    {
        var mask = options.MaskColumns ?? [];
        var drop = options.DropColumns ?? [];
        if ((mask.Count == 0 && drop.Count == 0) || result.Rows == null) return result;

        if (result.Columns != null)
        {
            var keep = result.Columns.Select(c => !MatchesAny(c, drop)).ToArray();
            var masked = result.Columns.Select(c => MatchesAny(c, mask)).ToArray();
            result.Columns = result.Columns.Where((_, i) => keep[i]).ToList();
            result.Rows = result.Rows.Select(row => row is IList cells and not byte[]
                ? (object)cells.Cast<object?>()
                    .Select((v, i) => i < masked.Length && masked[i] && v != null ? Mask : MaskValue(v, mask, drop))
                    .Where((_, i) => i >= keep.Length || keep[i])
                    .ToArray()
                : MaskValue(row, mask, drop)!).ToList();
        }
        else
        {
            result.Rows = result.Rows.Select(row => MaskValue(row, mask, drop)!).ToList();
        }
        return result;
    }

    /// <summary>The schema as the source shows it: allowed tables only, without dropped columns (a copy).</summary>
    public static DataExplorerSchemaResult FilterSchema(DataExplorerSchemaResult schema, DataExplorerProviderOptions options)
    {
        var allowed = options.AllowedObjects ?? [];
        var drop = options.DropColumns ?? [];
        return new DataExplorerSchemaResult
        {
            Source = schema.Source,
            Tables = schema.Tables
                .Where(t => allowed.Count == 0 || IsObjectAllowed(t.Name, allowed))
                .Select(t => drop.Count == 0
                    ? t
                    : new DataExplorerSchemaTable
                    {
                        Name = t.Name,
                        Type = t.Type,
                        Columns = t.Columns.Where(c => !MatchesAny(c.Name, drop)).ToList(),
                    })
                .ToList(),
        };
    }

    /// <summary><c>show collections</c> (MongoDB) lists only the allowed collections.</summary>
    public static DataExplorerResult FilterListing(DataExplorerResult result, DataExplorerProviderOptions options, string query)
    {
        var allowed = options.AllowedObjects;
        if (allowed == null || allowed.Count == 0 || result.Rows == null ||
            options.Language is not (DataExplorerLanguage.Shell or DataExplorerLanguage.Json) ||
            !MongoShowCollections().IsMatch(query.Trim()))
        {
            return result;
        }
        result.Rows = result.Rows.Where(row => ListingName(row) is { } name && IsObjectAllowed(name, allowed)).ToList();
        result.RowCount = result.Rows.Count;
        return result;
    }

    private static string? ListingName(object? row) => row switch
    {
        IDictionary dictionary when dictionary.Contains("name") => dictionary["name"] as string,
        JsonObject obj => obj["name"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null,
        _ => null,
    };
}

/// <summary>Sliding-window counter of data explorer executes per source and caller (<c>RateLimit</c>).</summary>
public sealed class DataExplorerRateLimiter
{
    private readonly TimeProvider _time;
    private readonly Dictionary<string, List<long>> _hits = new();

    public DataExplorerRateLimiter(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Records an execute; false when the caller is over the source's limit (not recorded then).</summary>
    public bool TryAcquire(DataExplorerProviderOptions options, DataExplorerCaller? caller)
    {
        var limit = options.RateLimit;
        if (limit == null || limit.MaxQueries <= 0 || limit.WindowSeconds <= 0) return true;
        var key = $"{options.Name}\u0000{caller?.UserId ?? caller?.RemoteAddress ?? "anonymous"}";
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var since = now - limit.WindowSeconds * 1000L;
        lock (_hits)
        {
            if (!_hits.TryGetValue(key, out var recent)) _hits[key] = recent = [];
            recent.RemoveAll(t => t <= since);
            if (recent.Count >= limit.MaxQueries) return false;
            recent.Add(now);
            return true;
        }
    }
}
