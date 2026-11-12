using System.Collections;
using System.Collections.Concurrent;
using System.Text;
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
    internal readonly record struct SqlToken(SqlTokenKind Kind, string Value, string Raw)
    {
        public bool IsPunct(string value) => Kind == SqlTokenKind.Punct && Value == value;
    }

    /// <summary>
    /// How a dialect reads quotes and comments. The data explorer does not know which database a <c>sql</c> source
    /// is, so a query is checked under the reading of each dialect (<see cref="SqlReadings"/>): a quote or comment
    /// one dialect skips cannot hide a statement another one runs.
    /// </summary>
    /// <param name="BackslashEscapes"><c>\'</c> escapes a quote in strings (MySQL; Postgres without <c>standard_conforming_strings</c>).</param>
    /// <param name="DoubleQuotedStrings"><c>"..."</c> is a string, not an identifier (MySQL without <c>ANSI_QUOTES</c>).</param>
    /// <param name="EStrings"><c>E'...'</c> strings take backslash escapes (Postgres).</param>
    /// <param name="DollarQuotes"><c>$tag$ ... $tag$</c> strings (Postgres).</param>
    /// <param name="NestedComments"><c>/* /* */ */</c> comments nest (Postgres, SQL Server).</param>
    /// <param name="MySqlComments"><c>#</c> starts a comment, <c>--</c> only before a space, and the text of <c>/*! ... */</c> runs (MySQL).</param>
    /// <param name="Brackets"><c>[name]</c> is a quoted identifier (SQL Server, SQLite).</param>
    /// <param name="UnicodeLettersOnly">Only letters and digits from U+0080 are part of a name (SQL Server); other dialects take every such character.</param>
    internal readonly record struct SqlReading(
        bool BackslashEscapes = false,
        bool DoubleQuotedStrings = false,
        bool EStrings = false,
        bool DollarQuotes = false,
        bool NestedComments = false,
        bool MySqlComments = false,
        bool Brackets = false,
        bool UnicodeLettersOnly = false);

    /// <summary>MySQL (with and without <c>ANSI_QUOTES</c> and <c>NO_BACKSLASH_ESCAPES</c>), Postgres (with and without <c>standard_conforming_strings</c>), SQL Server and SQLite.</summary>
    internal static readonly SqlReading[] SqlReadings =
    [
        new(BackslashEscapes: true, DoubleQuotedStrings: true, MySqlComments: true),
        new(BackslashEscapes: true, MySqlComments: true),
        new(DoubleQuotedStrings: true, MySqlComments: true),
        new(MySqlComments: true),
        new(EStrings: true, DollarQuotes: true, NestedComments: true),
        new(BackslashEscapes: true, EStrings: true, DollarQuotes: true, NestedComments: true),
        new(NestedComments: true, Brackets: true, UnicodeLettersOnly: true),
        new(Brackets: true),
    ];

    // A Postgres dollar tag is a name: '$a1$' and '$é$' too
    [GeneratedRegex(@"\G\$(?:[A-Za-z_\u0080-￿][A-Za-z0-9_\u0080-￿]*)?\$")]
    private static partial Regex DollarTag();

    [GeneratedRegex(@"\G/\*M?!\d*")]
    private static partial Regex MySqlExecutableComment();

    /// <summary>
    /// Splits SQL into words, quoted identifiers and punctuation as <paramref name="reading"/> reads it; comments
    /// and string literals are dropped, so keywords inside them do not count. A <c>"..."</c> string (MySQL without
    /// <c>ANSI_QUOTES</c>) stays a single name token, so it never hides a keyword and still names what it quotes.
    /// </summary>
    internal static List<SqlToken> TokenizeSql(string sql, SqlReading reading = default)
    {
        bool IsNameStart(char ch) => char.IsAsciiLetter(ch) || ch == '_' ||
            (ch >= '\u0080' && (!reading.UnicodeLettersOnly || char.IsLetter(ch)));
        bool IsNamePart(char ch) => IsNameStart(ch) || char.IsAsciiDigit(ch) || ch == '$' ||
            (ch >= '\u0080' && char.IsLetterOrDigit(ch));

        var tokens = new List<SqlToken>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            // Line comments; MySQL needs a space after '--' ('1--1' is 1 - -1), and '#' is a comment in MySQL only
            // ('#t' is a SQL Server temporary table, '#' XOR in Postgres)
            if ((c == '-' && next == '-' && (!reading.MySqlComments || i + 2 >= sql.Length ||
                    char.IsWhiteSpace(sql[i + 2]) || char.IsControl(sql[i + 2]))) ||
                (c == '#' && reading.MySqlComments))
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && next == '*')
            {
                var executable = reading.MySqlComments ? MySqlExecutableComment().Match(sql, i) : null;
                // MySQL runs the text of /*! ... */ (its */ is read as punctuation)
                i = executable is { Success: true } ? i + executable.Length : SkipBlockComment(sql, i, reading.NestedComments);
                continue;
            }
            if (c == '"' && reading.DoubleQuotedStrings)
            {
                // A string here, but every other dialect reads a name, and a string cannot stand where a table or a
                // column does: kept as that name, so FROM "Users" u WHERE ... still names the table, not WHERE or u
                var end = SkipString(sql, i, c, reading.BackslashEscapes);
                var name = sql[(i + 1)..Math.Max(i + 1, end - 1)].Replace("\"\"", "\"");
                tokens.Add(new SqlToken(SqlTokenKind.Ident, name, name));
                i = end;
                continue;
            }
            if (c == '\'')
            {
                i = SkipString(sql, i, c, reading.BackslashEscapes);
                continue;
            }
            if (c == '$' && reading.DollarQuotes)
            {
                var m = DollarTag().Match(sql, i);
                if (m.Success)
                {
                    var end = sql.IndexOf(m.Value, i + m.Length, StringComparison.Ordinal);
                    i = end < 0 ? sql.Length : end + m.Length;
                    continue;
                }
            }
            if (c is '"' or '`' || (c == '[' && reading.Brackets))
            {
                var (name, end) = QuotedIdentifier(sql, i, c == '[' ? ']' : c);
                tokens.Add(new SqlToken(SqlTokenKind.Ident, name, name));
                i = end;
                continue;
            }
            if (IsNameStart(c))
            {
                var j = i + 1;
                while (j < sql.Length && IsNamePart(sql[j])) j++;
                var raw = sql[i..j];
                if (reading.EStrings && raw is "E" or "e" && j < sql.Length && sql[j] == '\'')
                {
                    // Postgres escape string: E'it\'s'
                    i = SkipString(sql, j, '\'', true);
                    continue;
                }
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

    /// <summary>The index after the string whose opening quote is at <paramref name="i"/> (a doubled quote is an escape); the end of the text when it is not closed.</summary>
    private static int SkipString(string sql, int i, char quote, bool backslashEscapes)
    {
        i++;
        while (i < sql.Length)
        {
            if (sql[i] == quote && i + 1 < sql.Length && sql[i + 1] == quote) i += 2;
            else if (backslashEscapes && sql[i] == '\\') i += 2;
            else if (sql[i] == quote) return i + 1;
            else i++;
        }
        return sql.Length;
    }

    /// <summary>The index after the <c>/* ... */</c> comment starting at <paramref name="i"/>.</summary>
    private static int SkipBlockComment(string sql, int i, bool nested)
    {
        var depth = 0;
        while (i < sql.Length)
        {
            if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*' && (nested || depth == 0))
            {
                depth++;
                i += 2;
            }
            else if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
            {
                depth--;
                i += 2;
                if (depth == 0) return i;
            }
            else i++;
        }
        return sql.Length;
    }

    /// <summary>The name quoted at <paramref name="i"/> (<c>"a""b"</c>, <c>[a]]b]</c>: a doubled closing character is an escape) and the index after it.</summary>
    private static (string Name, int End) QuotedIdentifier(string sql, int i, char close)
    {
        var name = new StringBuilder();
        var j = i + 1;
        while (j < sql.Length)
        {
            var end = sql.IndexOf(close, j);
            if (end < 0) break;
            name.Append(sql, j, end - j);
            if (end + 1 < sql.Length && sql[end + 1] == close)
            {
                name.Append(close);
                j = end + 2;
                continue;
            }
            return (name.ToString(), end + 1);
        }
        if (j < sql.Length) name.Append(sql, j, sql.Length - j);
        return (name.ToString(), sql.Length);
    }

    /// <summary>A carriage return without a line feed: Postgres ends a <c>--</c> comment there, other databases do not.</summary>
    private static bool HasLoneCarriageReturn(string sql)
    {
        for (var i = sql.IndexOf('\r'); i >= 0; i = sql.IndexOf('\r', i + 1))
        {
            if (i + 1 >= sql.Length || sql[i + 1] != '\n') return true;
        }
        return false;
    }

    private static readonly HashSet<string> SqlReadStarts = ["SELECT", "WITH", "EXPLAIN", "SHOW", "DESCRIBE", "DESC", "VALUES", "TABLE"];

    /// <summary>
    /// Keywords of statements that write. <c>COMMIT</c> and <c>ROLLBACK</c> too: SQL Server runs statements without a
    /// <c>;</c> between them, and ending the read-only transaction would keep what follows.
    /// </summary>
    private static readonly HashSet<string> SqlWriteWords =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "UPSERT", "REPLACE", "CREATE", "ALTER", "DROP", "TRUNCATE", "GRANT",
        "REVOKE", "DENY", "COPY", "CALL", "EXEC", "EXECUTE", "INTO", "LOCK", "VACUUM", "ATTACH", "DETACH", "PRAGMA", "SET",
        "COMMIT", "ROLLBACK",
    ];

    /// <summary>Write keywords that are also read-only functions when a <c>(</c> follows (<c>REPLACE(name, 'a', 'b')</c>).</summary>
    private static readonly HashSet<string> SqlFunctionWords = ["REPLACE", "INSERT"];

    private static string? SqlReadOnlyError(string query)
    {
        if (HasLoneCarriageReturn(query)) return "Read-only source: end lines with \\n or \\r\\n";
        foreach (var reading in SqlReadings)
        {
            if (SqlReadOnlyError(TokenizeSql(query, reading)) is { } error) return error;
        }
        return null;
    }

    private static string? SqlReadOnlyError(List<SqlToken> tokens)
    {
        var statements = new List<List<SqlToken>> { new() };
        foreach (var t in tokens)
        {
            if (t.IsPunct(";")) statements.Add([]);
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
            if (SqlFunctionWords.Contains(t.Value) && i + 1 < statement.Count && statement[i + 1].IsPunct("(")) continue;
            return $"Read-only source: {t.Value} is not allowed";
        }
        return null;
    }

    /// <summary>Functions whose arguments use FROM without naming a table (<c>EXTRACT(YEAR FROM day)</c>).</summary>
    private static readonly HashSet<string> SqlFromFunctions = ["EXTRACT", "SUBSTRING", "SUBSTR", "TRIM", "POSITION", "OVERLAY"];

    private static readonly HashSet<string> SqlObjectKeywords = ["FROM", "JOIN", "INTO", "UPDATE", "TABLE", "USING"];

    /// <summary>Keywords that name the table after them when they start a statement (<c>DESCRIBE users</c>).</summary>
    private static readonly HashSet<string> SqlDescribeWords = ["DESCRIBE", "DESC", "EXPLAIN"];

    /// <summary>Words after EXPLAIN that are not a table (<c>EXPLAIN ANALYZE SELECT ...</c>).</summary>
    private static readonly HashSet<string> SqlExplainWords =
        ["SELECT", "WITH", "ANALYZE", "VERBOSE", "FORMAT", "EXTENDED", "PARTITIONS", "QUERY", "TABLE", "VALUES", "FOR",
         "INSERT", "UPDATE", "DELETE", "REPLACE", "MERGE"];

    /// <summary>Words that end the list of tables after FROM or USING.</summary>
    private static readonly HashSet<string> SqlFromListEnd =
    [
        "WHERE", "GROUP", "HAVING", "ORDER", "LIMIT", "OFFSET", "UNION", "EXCEPT", "INTERSECT", "WINDOW", "FETCH",
        "FOR", "RETURNING", "SELECT", "FROM", "QUALIFY", "OPTION", "INTO", "SET", "VALUES",
    ];

    private static readonly HashSet<string> SqlClauseWords =
    [
        "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "ON", "USING", "GROUP", "ORDER", "HAVING",
        "LIMIT", "OFFSET", "UNION", "EXCEPT", "INTERSECT", "WINDOW", "FETCH", "FOR", "NATURAL", "SET", "VALUES",
        "SELECT", "RETURNING", "TABLESAMPLE", "WITH",
    ];

    /// <summary>
    /// The tables a SQL query names (after FROM, JOIN, INTO, UPDATE, TABLE, USING, every item of a FROM list, and
    /// <c>DESCRIBE name</c>), without the CTE names it defines where they are in scope. Reads the query as each
    /// dialect does (<see cref="SqlReadings"/>) and returns every name any reading finds.
    /// </summary>
    public static List<string> SqlObjects(string query)
    {
        var names = new List<string>();
        foreach (var reading in SqlReadings)
        {
            foreach (var name in WalkSqlObjects(TokenizeSql(query, reading)).Objects)
            {
                if (!names.Contains(name)) names.Add(name);
            }
        }
        return names;
    }

    /// <summary>
    /// The tables the tokens name (without CTE names in scope); the lower-case names a row of them goes by (tables,
    /// CTEs, aliases); and the indices of the tokens that name or alias them.
    /// </summary>
    private static (List<string> Objects, HashSet<string> RowNames, HashSet<int> Defined) WalkSqlObjects(List<SqlToken> tokens)
    {
        SqlToken? At(int index) => index >= 0 && index < tokens.Count ? tokens[index] : null;
        static bool IsName(SqlToken? t) => t is { Kind: SqlTokenKind.Ident or SqlTokenKind.Word };
        bool IsPunctAt(int index, string value) => At(index) is { } t && t.IsPunct(value);

        // The end of the parentheses (or statement) that a token is in
        int ScopeEnd(int start)
        {
            var depth = 0;
            for (var e = start; e < tokens.Count; e++)
            {
                if (tokens[e].IsPunct("(")) depth++;
                else if (tokens[e].IsPunct(")") && --depth < 0) return e;
                else if (tokens[e].IsPunct(";") && depth == 0) return e;
            }
            return tokens.Count;
        }

        // CTE names: name [ (columns) ] AS ( ... ), in scope until the end of the parentheses or statement they are in
        var ctes = new List<(string Name, int Start, int End)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!IsName(tokens[i])) continue;
            var j = i + 1;
            if (IsPunctAt(j, "("))
            {
                var depth = 0;
                for (; j < tokens.Count; j++)
                {
                    if (tokens[j].IsPunct("(")) depth++;
                    else if (tokens[j].IsPunct(")") && --depth == 0) break;
                }
                j++;
            }
            if (At(j) is { Kind: SqlTokenKind.Word, Value: "AS" } && IsPunctAt(j + 1, "(")) ctes.Add((tokens[i].Raw, i, ScopeEnd(i)));
        }
        var rowNames = new HashSet<string>(ctes.Select(c => c.Name.ToLowerInvariant()));
        var defined = new HashSet<int>(ctes.Select(c => c.Start));
        bool IsCte(string name, int at) =>
            ctes.Any(c => c.Start < at && at < c.End && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        (string Name, int Next)? ReadName(int start)
        {
            if (!IsName(At(start))) return null;
            var parts = new List<string> { tokens[start].Raw };
            var k = start + 1;
            while (IsPunctAt(k, "."))
            {
                if (IsName(At(k + 1)))
                {
                    parts.Add(tokens[k + 1].Raw);
                    k += 2;
                }
                // SQL Server 'db..table' (the default schema)
                else if (IsPunctAt(k + 1, "."))
                {
                    parts.Add(string.Empty);
                    k++;
                }
                else break;
            }
            return (string.Join('.', parts), k);
        }

        // The next item of a FROM list after 'start': after a ',' in the same parentheses, before WHERE, GROUP, ...
        int NextListItem(int start)
        {
            var depth = 0;
            for (var k = start; k < tokens.Count; k++)
            {
                var t = tokens[k];
                if (t.IsPunct("(")) depth++;
                else if (t.IsPunct(")") && --depth < 0) return -1;
                else if (depth > 0) continue;
                else if (t.IsPunct(";")) return -1;
                else if (t.IsPunct(",")) return k + 1;
                else if (t.Kind == SqlTokenKind.Word && SqlFromListEnd.Contains(t.Value)) return -1;
            }
            return -1;
        }

        var objects = new List<string>();
        // Records the table named at 'at' (up to 'next') and its alias; returns the index after them
        int Add(string name, int at, int next)
        {
            if (!IsCte(name, at)) objects.Add(name);
            rowNames.Add(name.Split('.')[^1].ToLowerInvariant());
            for (var d = at; d < next; d++) defined.Add(d);
            // An alias: [AS] alias
            if (At(next) is { Kind: SqlTokenKind.Word, Value: "AS" })
            {
                if (IsName(At(next + 1)))
                {
                    rowNames.Add(tokens[next + 1].Raw.ToLowerInvariant());
                    defined.Add(next + 1);
                }
                return next + 2;
            }
            if (At(next) is { Kind: SqlTokenKind.Word } alias && !SqlClauseWords.Contains(alias.Value))
            {
                rowNames.Add(alias.Raw.ToLowerInvariant());
                defined.Add(next);
                return next + 1;
            }
            return next;
        }

        var openers = new Stack<string?>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.IsPunct("("))
            {
                var previous = At(i - 1);
                openers.Push(previous is { Kind: SqlTokenKind.Word } ? previous.Value.Value : null);
                continue;
            }
            if (t.IsPunct(")"))
            {
                openers.TryPop(out _);
                continue;
            }
            if (t.Kind != SqlTokenKind.Word) continue;
            // DESCRIBE users, EXPLAIN users (MySQL) at the start of a statement
            if (SqlDescribeWords.Contains(t.Value) && (i == 0 || tokens[i - 1].IsPunct(";")))
            {
                if (ReadName(i + 1) is { } described && !SqlExplainWords.Contains(tokens[i + 1].Value)) Add(described.Name, i + 1, described.Next);
                continue;
            }
            if (!SqlObjectKeywords.Contains(t.Value)) continue;
            if (t.Value == "FROM" && openers.TryPeek(out var opener) && opener != null && SqlFromFunctions.Contains(opener)) continue;
            var list = t.Value is "FROM" or "USING";
            var k = i + 1;
            // FROM a, (SELECT ...) b, f(x) c, d: every item of the list
            while (k >= 0)
            {
                if (At(k)?.Value is "ONLY" or "LATERAL") k++;
                if (ReadName(k) is { } name) k = Add(name.Name, k, name.Next);
                if (!list) break;
                k = NextListItem(k);
            }
        }
        return (objects, rowNames, defined);
    }

    // ── Masked columns ──────────────────────────────────────────────────

    /// <summary>Words never taken for column names when checking masked columns.</summary>
    private static readonly HashSet<string> SqlKeywords =
    [
        .. SqlReadStarts, .. SqlWriteWords, .. SqlObjectKeywords, .. SqlClauseWords,
        "AS", "DISTINCT", "ALL", "TOP", "BY", "AND", "OR", "NOT", "NULL", "IS", "IN", "LIKE", "ILIKE", "BETWEEN",
        "CASE", "WHEN", "THEN", "ELSE", "END", "ASC", "EXISTS", "ANY", "SOME", "TRUE", "FALSE", "ONLY", "LATERAL",
    ];

    /// <summary>What may come right before a masked column in the select list.</summary>
    private static readonly HashSet<string> SqlSelectItemBefore = ["SELECT", "DISTINCT", "ALL", "TOP"];

    private static readonly HashSet<string> SqlSetOperations = ["UNION", "EXCEPT", "INTERSECT", "MINUS"];

    /// <summary>Postgres identifiers with Unicode escapes (<c>U&amp;"\0065mail"</c>).</summary>
    [GeneratedRegex(@"[Uu]&""")]
    private static partial Regex UnicodeIdentifier();

    /// <summary>The refusal of a masked column that is not selected by its own name.</summary>
    public static string MaskedColumnMessage(string name) => $"Masked column '{name}' can only be selected by its own name";

    private static string WholeRowMessage(string name) =>
        $"'{name}' is a whole row and this source masks columns: select its columns by name";

    private const string UnicodeIdentifierMessage = "This source masks columns: write names without Unicode escapes (U&\"...\")";

    /// <summary>
    /// The masked or dropped columns a SQL query names, each with whether every mention, under every reading, is a
    /// plain item of the outer select list (<c>SELECT email</c>, <c>SELECT u.email,</c>): outside parentheses,
    /// before the first <c>FROM</c>, followed by <c>,</c>, <c>FROM</c> or the end, in a query without
    /// <c>UNION</c>/<c>EXCEPT</c>/<c>INTERSECT</c>/<c>MINUS</c>. The error refuses the query outright: a whole row
    /// used as a value (Postgres <c>SELECT u FROM users u</c>, <c>row_to_json(u)</c>) or a Unicode-escaped name.
    /// </summary>
    private static (List<(string Name, bool Plain)> Names, string? Error) MaskedScan(DataExplorerProviderOptions options, string query)
    {
        List<string> patterns = [.. options.MaskColumns ?? [], .. options.DropColumns ?? []];
        if (patterns.Count == 0 || options.Language != DataExplorerLanguage.Sql) return ([], null);
        if (UnicodeIdentifier().IsMatch(query)) return ([], UnicodeIdentifierMessage);

        // lower-case name -> (as first written, every mention plain)
        var found = new Dictionary<string, (string Name, bool Plain)>();
        foreach (var reading in SqlReadings)
        {
            var tokens = TokenizeSql(query, reading);
            var (_, rowNames, defined) = WalkSqlObjects(tokens);
            var setOperation = tokens.Any(t => t.Kind == SqlTokenKind.Word && SqlSetOperations.Contains(t.Value));
            var depth = 0;
            var inSelectList = false;
            for (var i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.IsPunct("("))
                {
                    depth++;
                    continue;
                }
                if (t.IsPunct(")"))
                {
                    depth--;
                    continue;
                }
                if (t.IsPunct(";"))
                {
                    inSelectList = false;
                    continue;
                }
                if (depth == 0 && t.Kind == SqlTokenKind.Word && t.Value is "SELECT" or "FROM") inSelectList = t.Value == "SELECT";
                if (defined.Contains(i) || t.Kind == SqlTokenKind.Punct || (t.Kind == SqlTokenKind.Word && SqlKeywords.Contains(t.Value))) continue;
                SqlToken? next = i + 1 < tokens.Count ? tokens[i + 1] : null;
                // A qualifier (u.) or a function (upper()
                if (next is { } n && (n.IsPunct(".") || n.IsPunct("("))) continue;
                var qualified = i > 0 && tokens[i - 1].IsPunct(".");
                if (!qualified && rowNames.Contains(t.Raw.ToLowerInvariant())) return ([], WholeRowMessage(t.Raw));
                if (!MatchesAny(t.Raw, patterns)) continue;
                var k = qualified && i >= 2 && tokens[i - 2].Kind != SqlTokenKind.Punct ? i - 2 : i;
                SqlToken? before = k >= 1 ? tokens[k - 1] : null;
                var plain = depth == 0 && inSelectList && !setOperation &&
                    before is { } b && (b.IsPunct(",") || (b.Kind == SqlTokenKind.Word && SqlSelectItemBefore.Contains(b.Value))) &&
                    (next is not { } after || after.IsPunct(",") || after.IsPunct(";") ||
                        after is { Kind: SqlTokenKind.Word, Value: "FROM" });
                var key = t.Raw.ToLowerInvariant();
                var (name, allPlain) = found.TryGetValue(key, out var seen) ? seen : (t.Raw, true);
                found[key] = (name, allPlain && plain);
            }
        }
        return (found.Values.ToList(), null);
    }

    /// <summary>
    /// Why a SQL query is refused under <c>MaskColumns</c>/<c>DropColumns</c> (before it runs): it names a masked or
    /// dropped column other than as a plain item of the select list (<c>SELECT email AS e</c>, <c>upper(email)</c>,
    /// <c>WHERE email = ...</c>), uses a whole row as a value, or escapes a name; null otherwise.
    /// </summary>
    public static string? MaskedColumnError(DataExplorerProviderOptions options, string query)
    {
        var (names, error) = MaskedScan(options, query);
        if (error != null) return error;
        var notPlain = names.FirstOrDefault(n => !n.Plain);
        return notPlain.Name != null ? MaskedColumnMessage(notPlain.Name) : null;
    }

    /// <summary>
    /// Why a result is withheld: a masked or dropped column the query selects is missing from the result's columns
    /// (it came back under another name, e.g. SQL Server <c>FOR JSON</c>); null otherwise. <paramref name="columns"/>
    /// are the result's columns before masking.
    /// </summary>
    public static string? MaskedResultError(DataExplorerProviderOptions options, string query, IReadOnlyCollection<string>? columns)
    {
        var present = new HashSet<string>(columns ?? [], StringComparer.OrdinalIgnoreCase);
        var missing = MaskedScan(options, query).Names.FirstOrDefault(n => !present.Contains(n.Name));
        return missing.Name != null ? MaskedColumnMessage(missing.Name) : null;
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

    [GeneratedRegex(@"\\u([0-9A-Fa-f]{4})")]
    private static partial Regex JsonUnicodeEscape();

    /// <summary>Stages that read or write other collections.</summary>
    [GeneratedRegex(@"\$(lookup|graphLookup|unionWith|out|merge)\b")]
    private static partial Regex MongoCollectionStage();

    /// <summary>Where those stages name a collection: <c>from</c>, <c>coll</c>, <c>into</c>, <c>$unionWith</c>, <c>$out</c>, <c>$merge</c>.</summary>
    [GeneratedRegex(@"[""']?(?:\b(?:from|coll|into)|\$(?:unionWith|out|merge))[""']?\s*:\s*(?:""([^""]*)""|'([^']*)'|(\{))?")]
    private static partial Regex MongoCollectionKey();

    /// <summary>The query with JSON <c>\uXXXX</c> escapes decoded, as the driver reads it (<c>"\u0024out"</c> is <c>$out</c>).</summary>
    private static string DecodeJsonEscapes(string query) =>
        JsonUnicodeEscape().Replace(query, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    private static string? MongoReadOnlyError(string query)
    {
        var q = query.Trim();
        if (MongoShow().IsMatch(q)) return null;
        var m = MongoCall().Match(q);
        if (!m.Success || !MongoReadMethods.Contains(m.Groups[3].Value))
        {
            return "Read-only source: only find, findOne, countDocuments, estimatedDocumentCount, distinct and aggregate are allowed";
        }
        if (m.Groups[3].Value.Equals("aggregate", StringComparison.OrdinalIgnoreCase) && MongoWriteStage().IsMatch(DecodeJsonEscapes(q)))
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
        var names = new List<string> { m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value };
        // $lookup, $graphLookup and $unionWith read other collections, $out and $merge write them
        var text = DecodeJsonEscapes(q);
        if (!MongoCollectionStage().IsMatch(text)) return names;
        foreach (Match key in MongoCollectionKey().Matches(text))
        {
            if (key.Groups[1].Success) names.Add(key.Groups[1].Value);
            else if (key.Groups[2].Success) names.Add(key.Groups[2].Value);
            // A collection the query does not name as a string ($lookup: { from: someVariable })
            else if (!key.Groups[3].Success) return null;
        }
        return names;
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

    /// <summary>The words of a Redis command as the provider reads them: quotes group text (<c>a"b c"</c> is <c>ab c</c>), whitespace splits.</summary>
    private static List<string> RedisWords(string query)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var ch in query.Trim())
        {
            if (quote != null)
            {
                if (ch == quote) quote = null;
                else current.Append(ch);
            }
            else if (ch is '"' or '\'') quote = ch;
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length == 0) continue;
                words.Add(current.ToString());
                current.Clear();
            }
            else current.Append(ch);
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

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

    /// <summary>A key that names an index in a request body (<c>_msearch</c> headers, <c>_mget</c> docs, terms lookups).</summary>
    [GeneratedRegex(@"""_?index""\s*:")]
    private static partial Regex EsBodyIndex();

    /// <summary>The path without the query string and fragment (the HTTP client drops <c>#...</c>).</summary>
    private static string EsBarePath(string path) => path.Split('?', '#')[0];

    private static string? EsReadOnlyError(string query)
    {
        var (verb, path) = EsRequestLine(query);
        if (verb is "GET" or "HEAD") return null;
        var bare = EsBarePath(path);
        var read = EsReadPost().Match(bare);
        // Only an index (or _all) before the endpoint: '/logs/_doc/_search' indexes a document
        if (verb == "POST" && read.Success &&
            bare[..read.Index].Split('/', StringSplitOptions.RemoveEmptyEntries).All(s => !s.StartsWith('_') || s == "_all"))
        {
            return null;
        }
        return $"Read-only source: {verb} {bare} is not allowed";
    }

    private static List<string>? EsObjects(string query)
    {
        var (_, path) = EsRequestLine(query);
        var first = EsBarePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(first) || first.StartsWith('_')) return null;
        // A body that names other indices
        var lines = query.Trim().Split('\n', 2);
        if (lines.Length > 1 && EsBodyIndex().IsMatch(DecodeJsonEscapes(lines[1]))) return null;
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
        DataExplorerLanguage.Sql => HasLoneCarriageReturn(query) ? null : SqlObjects(query),
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
            case string text when text.TrimStart() is ['{' or '[', ..]:
                return MaskJsonText(text, mask, drop);
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

    /// <summary>JSON text in a cell (a JSON column, an Elasticsearch response) masked and dropped like a document; other text as it is.</summary>
    private static string MaskJsonText(string text, List<string> mask, List<string> drop)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return text;
        }
        if (node is not (JsonObject or JsonArray)) return text;
        var masked = ((JsonNode)MaskValue(node, mask, drop)!).ToJsonString();
        return masked == node.ToJsonString() ? text : masked;
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
