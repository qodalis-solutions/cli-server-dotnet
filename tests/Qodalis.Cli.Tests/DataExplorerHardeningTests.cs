using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.DataExplorer.Postgres;
using Qodalis.Cli.Plugin.DataExplorer.Sql;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;
using L = Qodalis.Cli.Abstractions.DataExplorer.DataExplorerLanguage;
using P = Qodalis.Cli.Services.DataExplorerPolicy;

namespace Qodalis.Cli.Tests;

// Data explorer hardening (spec: web-cli docs/superpowers/specs/2026-11-12-data-explorer-hardening-design.md):
// per-source permissions, read-only sources, allowed objects, masking, audit and rate limits.

/// <summary>A provider that records the queries it runs and answers a fixed table.</summary>
internal sealed class RecordingDataExplorerProvider(bool readOnlyQueries = false) : IDataExplorerProvider
{
    public List<string> Queries { get; } = [];

    public bool ReadOnlyQueries => readOnlyQueries;

    public Task<DataExplorerResult> ExecuteAsync(DataExplorerExecutionContext context, CancellationToken cancellationToken = default)
    {
        Queries.Add(context.Query);
        return Task.FromResult(new DataExplorerResult
        {
            Success = true,
            Source = context.Options.Name,
            Columns = ["id", "email", "password"],
            Rows = [new object?[] { 1, "a@x.io", "h1" }!, new object?[] { 2, null, "h2" }!],
        });
    }

    public Task<DataExplorerSchemaResult?> GetSchemaAsync(DataExplorerProviderOptions options, CancellationToken cancellationToken = default) =>
        Task.FromResult<DataExplorerSchemaResult?>(new DataExplorerSchemaResult
        {
            Source = options.Name,
            Tables =
            [
                new DataExplorerSchemaTable
                {
                    Name = "users", Type = "table",
                    Columns = [new() { Name = "id", Type = "int" }, new() { Name = "password", Type = "text" }],
                },
                new DataExplorerSchemaTable { Name = "secrets", Type = "table" },
            ],
        });
}

/// <summary>An audit store that keeps entries in a list (and can fail).</summary>
internal sealed class ListAuditStore(bool fail = false) : IAuditStore
{
    public List<AuditEntry> Entries { get; } = [];

    public Task LogAsync(AuditEntry entry)
    {
        if (fail) throw new InvalidOperationException("audit store down");
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<List<AuditEntry>> GetEntriesAsync(AuditQueryOptions? options = null) => Task.FromResult(Entries.ToList());

    public Task ClearAsync()
    {
        Entries.Clear();
        return Task.CompletedTask;
    }
}

public class DataExplorerReadOnlyRulesTests
{
    [Theory]
    [InlineData("SELECT * FROM users")]
    [InlineData("select 1; ")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("SELECT 'DELETE FROM users' AS s")]
    [InlineData("EXPLAIN SELECT * FROM users")]
    [InlineData("-- DROP TABLE users\nSELECT 1")]
    [InlineData("SHOW TABLES")]
    [InlineData("SELECT REPLACE(name, 'a', 'b') FROM users")]
    [InlineData("SELECT * FROM #recent")]
    [InlineData("SELECT 'it''s'")]
    [InlineData("SELECT 1 # trailing MySQL comment")]
    [InlineData("SELECT E'it\\'s' AS s")]
    [InlineData("SELECT \"a\"\"b\", [c]]d], `e``f` FROM t")]
    [InlineData("SELECT 1 /* a /* nested */ b */")]
    [InlineData("SELECT $1::int")]
    [InlineData("SELECT 1\r\nFROM t")]
    [InlineData("SELECT café FROM t")]
    public void Sql_AllowsReads(string query) => Assert.Null(P.ReadOnlyError(L.Sql, query));

    [Theory]
    [InlineData("DELETE FROM users")]
    [InlineData("update users set a = 1")]
    [InlineData("INSERT INTO users VALUES (1)")]
    [InlineData("DROP TABLE users")]
    [InlineData("SELECT 1; DELETE FROM users")]
    [InlineData("WITH x AS (DELETE FROM users RETURNING *) SELECT * FROM x")]
    [InlineData("SELECT * INTO copy FROM users")]
    [InlineData("TRUNCATE users")]
    [InlineData("CREATE TABLE t (a int)")]
    [InlineData("EXEC sp_who")]
    // Backslash is not an escape in standard SQL: the literal ends at \' and the DROP runs
    [InlineData("SELECT 'a\\'; DROP TABLE users; --'")]
    // $tag$ is not a quote outside Postgres
    [InlineData("SELECT $a$; DROP TABLE users; $a$")]
    [InlineData("SELECT * FROM #t; DROP TABLE users")]
    [InlineData("SELECT $$; DROP TABLE users; $$")]
    // MySQL: '--' needs a space ('1 --1' is 1 - -1), /*! ... */ runs, "..." is a string
    [InlineData("SELECT 1 --1; COMMIT; DELETE FROM users")]
    [InlineData("SELECT 1 /*! INTO OUTFILE '/tmp/x' */")]
    [InlineData("SELECT \"\\\"\" INTO OUTFILE '/tmp/f' -- \"")]
    // Postgres and SQL Server comments nest; SQL Server escapes ] as ]]
    [InlineData("SELECT 1 /* /* */ ' */ COMMIT DROP TABLE users --'")]
    [InlineData("SELECT [x]]'] , 1 DROP TABLE users --'")]
    // '#' is a comment only in MySQL
    [InlineData("SELECT 1 # x\n; DROP TABLE users")]
    // Postgres: [ is an array subscript, '$a1$' a dollar quote, a lone \r ends a '--' comment
    [InlineData("SELECT ('{1}'::int[])[length(']')] ; COMMIT; DELETE FROM users; --'")]
    [InlineData("SELECT $a1$ ' $a1$; DELETE FROM users; -- '")]
    [InlineData("SELECT 1 --x\r; DELETE FROM users")]
    // SQL Server runs statements without ';': ending the rolled-back transaction would keep the next one
    [InlineData("SELECT 1 COMMIT")]
    [InlineData("SELECT 1 ROLLBACK")]
    [InlineData("SELECT 1 DENY SELECT ON users TO public")]
    public void Sql_RefusesWrites(string query) => Assert.NotNull(P.ReadOnlyError(L.Sql, query));

    [Fact]
    public void MongoDb_AllowsFinds_RefusesWrites()
    {
        Assert.Null(P.ReadOnlyError(L.Shell, "db.users.find({})"));
        Assert.Null(P.ReadOnlyError(L.Shell, "show collections"));
        Assert.Null(P.ReadOnlyError(L.Shell, "db.getCollection('users').countDocuments({})"));
        Assert.NotNull(P.ReadOnlyError(L.Shell, "db.users.insertOne({a:1})"));
        Assert.NotNull(P.ReadOnlyError(L.Shell, "db.users.drop()"));
        Assert.NotNull(P.ReadOnlyError(L.Shell, "db.users.aggregate([{ $out: \"x\" }])"));
        // JSON escapes are read as the driver reads them
        Assert.NotNull(P.ReadOnlyError(L.Shell, "db.users.aggregate([{ \"\\u0024out\": \"x\" }])"));
        // MongoDB sources registered with the json language follow the same rules
        Assert.Null(P.ReadOnlyError(L.Json, "db.users.find({})"));
        Assert.NotNull(P.ReadOnlyError(L.Json, "db.users.deleteMany({})"));
    }

    [Fact]
    public void Redis_AllowsReads_RefusesWrites()
    {
        Assert.Null(P.ReadOnlyError(L.Redis, "GET rates:usd"));
        Assert.Null(P.ReadOnlyError(L.Redis, "hgetall h"));
        Assert.NotNull(P.ReadOnlyError(L.Redis, "SET a 1"));
        Assert.NotNull(P.ReadOnlyError(L.Redis, "FLUSHALL"));
    }

    [Fact]
    public void Elasticsearch_AllowsGetAndSearchPosts_RefusesWrites()
    {
        Assert.Null(P.ReadOnlyError(L.Elasticsearch, "GET /logs/_search"));
        Assert.Null(P.ReadOnlyError(L.Elasticsearch, "POST /logs/_search\n{}"));
        Assert.NotNull(P.ReadOnlyError(L.Elasticsearch, "DELETE /logs"));
        Assert.NotNull(P.ReadOnlyError(L.Elasticsearch, "POST /logs/_doc\n{}"));
        // The HTTP client drops the fragment; a document id that looks like an endpoint
        Assert.NotNull(P.ReadOnlyError(L.Elasticsearch, "POST /logs/_delete_by_query#/_search\n{}"));
        Assert.NotNull(P.ReadOnlyError(L.Elasticsearch, "POST /logs/_doc/_search\n{}"));
        Assert.Null(P.ReadOnlyError(L.Elasticsearch, "POST /_all/_search\n{}"));
        Assert.Null(P.ReadOnlyError(L.Elasticsearch, "POST /logs/_validate/query\n{}"));
    }

    [Fact]
    public void OtherLanguages_CannotBeChecked() =>
        Assert.Contains("readOnly: false", P.ReadOnlyError(L.Graphql, "{ users { id } }"));
}

public class DataExplorerAllowedObjectsTests
{
    private static DataExplorerProviderOptions Opts(Action<DataExplorerProviderOptions>? configure = null)
    {
        var o = new DataExplorerProviderOptions { Name = "db", Description = "Test" };
        configure?.Invoke(o);
        return o;
    }

    [Fact]
    public void ReadsTheTablesASqlQueryNames()
    {
        Assert.Equal(["orders", "public.users"],
            P.SqlObjects("SELECT * FROM public.users u JOIN orders o ON o.uid = u.id").Order());
        Assert.Equal(["users"], P.SqlObjects("WITH recent AS (SELECT * FROM users) SELECT * FROM recent"));
        Assert.Empty(P.SqlObjects("SELECT EXTRACT(YEAR FROM day) AS y"));
        Assert.Equal(["dbo.users"], P.SqlObjects("SELECT * FROM [dbo].[users]"));
        Assert.Equal(["Users", "dbo.orders"], P.SqlObjects("SELECT * FROM \"Users\", [dbo].[orders]"));
        Assert.Equal(["sec\"ret"], P.SqlObjects("SELECT * FROM \"sec\"\"ret\""));
        Assert.Equal(["état"], P.SqlObjects("SELECT * FROM état"));
        Assert.Empty(P.SqlObjects("EXPLAIN ANALYZE SELECT 1"));
    }

    [Theory]
    // MySQL without ANSI_QUOTES reads "Users" as a string; the table is still Users, never WHERE or the alias
    [InlineData("SELECT \"Id\" FROM \"Users\" WHERE \"CountryCode\" = 'RO'")]
    [InlineData("SELECT u.\"Id\" FROM \"Users\" u WHERE u.\"CountryCode\" = 'RO' ORDER BY 1")]
    [InlineData("SELECT * FROM \"Users\" AS u JOIN \"Bots\" b ON b.\"UserId\" = u.\"Id\"")]
    [InlineData("SELECT \"Email\", \"CountryCode\" FROM \"Users\" LIMIT 3")]
    [InlineData("SELECT \"CountryCode\", count(*) FROM \"Users\" GROUP BY \"CountryCode\" ORDER BY 2 DESC LIMIT 5")]
    public void AcceptsQuotedNamesOfAllowedTables(string query) =>
        Assert.Null(P.ObjectsError(Opts(o => o.AllowedObjects = ["Users", "Bots"]), query));

    [Theory]
    // Read as each dialect reads its comments and brackets
    [InlineData("SELECT * FROM allowed /* /* */ ' */ , secret --'")]
    [InlineData("SELECT arr[(SELECT 1 FROM secret)] FROM allowed")]
    // Every item of a FROM list, after subqueries and functions too
    [InlineData("SELECT * FROM (SELECT 1) a, secret")]
    [InlineData("SELECT * FROM allowed a, generate_series(1, 2) g, secret")]
    // A CTE name only covers its own parentheses and statement
    [InlineData("SELECT * FROM allowed WHERE EXISTS (WITH secret AS (SELECT 1) SELECT 1) UNION SELECT * FROM secret")]
    [InlineData("SELECT * FROM allowed..secret")]
    [InlineData("DESCRIBE secret")]
    [InlineData("EXPLAIN secret")]
    [InlineData("DELETE FROM allowed USING secret WHERE 1=1")]
    // Found by the Node review: a quote or comment one dialect reads differently
    [InlineData("SELECT 1 $x$, '\\'' a FROM secret WHERE ''='' -- $x$")]
    [InlineData("SELECT \"\\\"\" a FROM secret WHERE \"\"=\"\"")]
    [InlineData("SELECT 1 #'\n FROM secret -- '")]
    [InlineData("SELECT a--1, b FROM secret")]
    [InlineData("SELECT 1 /*! FROM secret */")]
    [InlineData("SELECT '\\', $x$ ' $x$ FROM secret -- '")]
    [InlineData("SELECT $a1$ ' $a1$ FROM secret -- '")]
    [InlineData("SELECT E'\\'' FROM secret -- '")]
    [InlineData("SELECT 1 # 2 FROM secret")]
    [InlineData("SELECT (ARRAY[1,2])[(SELECT length(']'))] FROM secret")]
    public void FindsEveryTableAQueryReads(string query)
    {
        Assert.Contains(P.SqlObjects(query), o => o.Split('.')[^1] == "secret");
        Assert.NotNull(P.ObjectsError(Opts(o => o.AllowedObjects = ["allowed"]), query));
    }

    [Fact]
    public void FindsTheCollectionsAPipelineReads_AndTheKeysTheProviderReads()
    {
        var mongo = Opts(o => { o.Language = L.Shell; o.AllowedObjects = ["users"]; });
        Assert.Null(P.ObjectsError(mongo, "db.users.aggregate([{ $match: {} }])"));
        Assert.Contains("secret", P.ObjectsError(mongo, "db.users.aggregate([{ $lookup: { from: \"secret\", localField: \"a\", foreignField: \"b\", as: \"s\" } }])"));
        Assert.Contains("secret", P.ObjectsError(mongo, "db.users.aggregate([{ \"$unionWith\": \"secret\" }])"));
        Assert.Contains("secret", P.ObjectsError(mongo, "db.users.aggregate([{ $graphLookup: { from: \"secret\", startWith: \"$a\", connectFromField: \"a\", connectToField: \"b\", as: \"s\" } }])"));
        // A collection that is not named as a string cannot be checked
        Assert.Contains("name the objects", P.ObjectsError(mongo, "db.users.aggregate([{ $lookup: { from: name, as: \"s\" } }])"));
        Assert.Contains("secret", P.ObjectsError(mongo, "db.users.aggregate([{ \"$unionWith\": { \"coll\": \"secret\" } }])"));
        Assert.Contains("secret", P.ObjectsError(mongo, "db.users.aggregate([{ \"\\u0024lookup\": { \"from\": \"secret\" } }])"));

        // The provider joins quoted text to the word around it
        var redis = Opts(o => { o.Language = L.Redis; o.AllowedObjects = ["rates:usd"]; });
        Assert.Null(P.ObjectsError(redis, "GET rates:usd"));
        Assert.NotNull(P.ObjectsError(redis, "GET 'rates:usd'x"));

        // Index names in a request body
        var es = Opts(o => { o.Language = L.Elasticsearch; o.AllowedObjects = ["logs"]; });
        Assert.Null(P.ObjectsError(es, "POST /logs/_search\n{\"query\":{\"match_all\":{}}}"));
        Assert.NotNull(P.ObjectsError(es, "POST /logs/_msearch\n{\"index\":\"secret\"}\n{}"));
    }

    [Fact]
    public void MatchesQualifiedAndUnqualifiedPatterns()
    {
        Assert.True(P.IsObjectAllowed("public.users", ["users"]));
        Assert.True(P.IsObjectAllowed("public.users", ["public.*"]));
        Assert.False(P.IsObjectAllowed("audit.users", ["public.*"]));
        Assert.True(P.IsObjectAllowed("rates:usd", ["rates:*"]));
        Assert.True(P.IsObjectAllowed("USERS", ["users"]));
    }

    [Fact]
    public void RefusesQueriesThatReadOtherObjects()
    {
        var o = Opts(o => o.AllowedObjects = ["users", "orders"]);
        Assert.Null(P.ObjectsError(o, "SELECT * FROM users"));
        Assert.Contains("'secrets'", P.ObjectsError(o, "SELECT * FROM users JOIN secrets ON 1=1"));
        Assert.Contains("session:1", P.ObjectsError(Opts(o => { o.Language = L.Redis; o.AllowedObjects = ["rates:*"]; }), "GET session:1"));
        Assert.Contains("secrets", P.ObjectsError(Opts(o => { o.Language = L.Shell; o.AllowedObjects = ["users"]; }), "db.secrets.find()"));
        Assert.Contains("name the objects", P.ObjectsError(Opts(o => { o.Language = L.Redis; o.AllowedObjects = ["rates:*"]; }), "SCAN 0"));
        Assert.Contains("logs", P.ObjectsError(Opts(o => { o.Language = L.Elasticsearch; o.AllowedObjects = ["metrics-*"]; }), "GET /logs/_search"));
    }

    [Fact]
    public void FiltersTheSchema_AllowedTables_NoDroppedColumns()
    {
        var schema = new DataExplorerSchemaResult
        {
            Source = "db",
            Tables =
            [
                new() { Name = "users", Type = "table", Columns = [new() { Name = "id", Type = "int" }, new() { Name = "password", Type = "text" }] },
                new() { Name = "secrets", Type = "table" },
            ],
        };
        var filtered = P.FilterSchema(schema, Opts(o => { o.AllowedObjects = ["users"]; o.DropColumns = ["password"]; }));
        Assert.Equal(["users"], filtered.Tables.Select(t => t.Name));
        Assert.Equal(["id"], filtered.Tables[0].Columns.Select(c => c.Name));
        // The provider's schema is not changed
        Assert.Equal(2, schema.Tables[0].Columns.Count);
    }

    [Fact]
    public void MasksAndDropsColumns_InTableAndDocumentResults()
    {
        var o = Opts(o => { o.MaskColumns = ["email"]; o.DropColumns = ["pass*"]; });
        var table = P.ApplyColumnPolicy(new DataExplorerResult
        {
            Source = "db",
            Columns = ["id", "email", "password"],
            Rows = [new object?[] { 1, "a@x.io", "h1" }!, new object?[] { 2, null, "h2" }!],
        }, o);
        Assert.Equal(["id", "email"], table.Columns!);
        Assert.Equal(JsonSerializer.Serialize(new object?[] { new object?[] { 1, "***" }, new object?[] { 2, null } }),
            JsonSerializer.Serialize(table.Rows));

        var docs = P.ApplyColumnPolicy(new DataExplorerResult
        {
            Source = "db",
            Rows =
            [
                new Dictionary<string, object>
                {
                    ["id"] = 1, ["email"] = "a@x.io", ["password"] = "h",
                    ["nested"] = new Dictionary<string, object> { ["email"] = "b@x.io" },
                    ["list"] = new List<object> { new Dictionary<string, object> { ["password"] = "x", ["ok"] = true } },
                },
            ],
        }, o);
        Assert.Equal("""[{"id":1,"email":"***","nested":{"email":"***"},"list":[{"ok":true}]}]""", JsonSerializer.Serialize(docs.Rows));

        // JSON text (a JSON column, an Elasticsearch response) like a document; other text as it is
        var text = P.ApplyColumnPolicy(new DataExplorerResult
        {
            Source = "db",
            Columns = ["response"],
            Rows = [new object?[] { """{"_source":{"email":"a@x.io","password":"h","id":1}}""" }!, new object?[] { "[not json" }!],
        }, o);
        Assert.Equal("""[["{\u0022_source\u0022:{\u0022email\u0022:\u0022***\u0022,\u0022id\u0022:1}}"],["[not json"]]""",
            JsonSerializer.Serialize(text.Rows));
    }

    [Fact]
    public void ShowCollections_ListsOnlyAllowedCollections()
    {
        var result = new DataExplorerResult
        {
            Source = "mongo",
            Rows = [new Dictionary<string, object> { ["name"] = "users" }, new Dictionary<string, object> { ["name"] = "secrets" }],
        };
        P.FilterListing(result, Opts(o => { o.Language = L.Shell; o.AllowedObjects = ["users"]; }), "show collections");
        Assert.Single(result.Rows!);
        Assert.Equal(1, result.RowCount);
    }
}

public class DataExplorerNameCaseHintTests
{
    private static readonly DataExplorerSchemaResult Schema = new()
    {
        Source = "db",
        Tables =
        [
            new DataExplorerSchemaTable { Name = "public.Users", Type = "table", Columns = [new DataExplorerSchemaColumn { Name = "CountryCode", Type = "text" }] },
            new DataExplorerSchemaTable { Name = "job_states", Type = "table", Columns = [new DataExplorerSchemaColumn { Name = "status", Type = "text" }] },
        ],
    };

    [Theory]
    [InlineData("42P01: relation \"users\" does not exist\n\nPOSITION: 22", "\"Users\"")]
    [InlineData("42703: column \"countrycode\" does not exist", "\"CountryCode\"")]
    [InlineData("42703: column u.countrycode does not exist", "\"CountryCode\"")]
    public void NamesTheTableOrColumnWithItsCapitals(string error, string quoted) =>
        Assert.Equal($"Names with capital letters need double quotes: {quoted}", P.NameCaseHint(error, Schema));

    [Theory]
    // Nothing differs only in case, or the name is not shown (a hidden table is never named)
    [InlineData("42P01: relation \"job_states\" does not exist")]
    [InlineData("42P01: relation \"onetimecodes\" does not exist")]
    [InlineData("syntax error at or near \"FORM\"")]
    public void SaysNothingElse(string error) => Assert.Null(P.NameCaseHint(error, Schema));
}

public class DataExplorerFixNameCaseTests
{
    private static readonly DataExplorerSchemaResult Schema = new()
    {
        Source = "db",
        Tables =
        [
            new DataExplorerSchemaTable
            {
                Name = "public.Users", Type = "table",
                Columns = [new DataExplorerSchemaColumn { Name = "CountryCode", Type = "text" }, new DataExplorerSchemaColumn { Name = "Count", Type = "int" }],
            },
            new DataExplorerSchemaTable { Name = "job_states", Type = "table", Columns = [new DataExplorerSchemaColumn { Name = "status", Type = "text" }] },
        ],
    };

    private static string Ansi(string name) => $"\"{name}\"";

    [Theory]
    [InlineData("select count(*) from Users", "relation \"users\" does not exist", "select count(*) from \"Users\"")]
    [InlineData("SELECT * FROM USERS -- Users\nWHERE 1 = 1", "relation \"users\" does not exist", "SELECT * FROM \"Users\" -- Users\nWHERE 1 = 1")]
    [InlineData("SELECT * FROM \"Users\" u WHERE u.countrycode = 'countrycode'", "column u.countrycode does not exist",
        "SELECT * FROM \"Users\" u WHERE u.\"CountryCode\" = 'countrycode'")]
    [InlineData("SELECT count(*), count FROM \"Users\"", "column \"count\" does not exist", "SELECT count(*), \"Count\" FROM \"Users\"")]
    public void WritesTheNameAsTheSchemaSpellsIt(string query, string error, string fixedQuery) =>
        Assert.Equal(fixedQuery, P.FixNameCase(query, error, Schema, Ansi));

    [Fact]
    public void QuotesTheWayTheSourceDoes() =>
        Assert.Equal("select * from [Users]", P.FixNameCase("select * from users", "Invalid object name 'users'.", Schema, n => $"[{n}]"));

    [Theory]
    // No name differing only in case, a name already quoted, a hidden table, another error
    [InlineData("select * from job_states", "relation \"job_states\" does not exist")]
    [InlineData("select * from \"users\"", "relation \"users\" does not exist")]
    [InlineData("select * from onetimecodes", "relation \"onetimecodes\" does not exist")]
    [InlineData("select * form Users", "syntax error at or near \"form\"")]
    // Only inside a string under some dialect's reading (MySQL backslash escapes)
    [InlineData("select 'it\\' Users' from t", "relation \"users\" does not exist")]
    public void LeavesOtherQueriesAlone(string query, string error) => Assert.Null(P.FixNameCase(query, error, Schema, Ansi));
}

/// <summary>A PostgreSQL-like source: names without quotes are folded to lower case and the table is "Users".</summary>
internal sealed class CaseFoldingDataExplorerProvider : IDataExplorerProvider
{
    public List<string> Queries { get; } = [];

    public Task<DataExplorerResult> ExecuteAsync(DataExplorerExecutionContext context, CancellationToken cancellationToken = default)
    {
        Queries.Add(context.Query);
        if (!context.Query.Contains("\"Users\"")) throw new InvalidOperationException("42P01: relation \"users\" does not exist");
        if (context.Query.Contains("countrycode", StringComparison.OrdinalIgnoreCase) && !context.Query.Contains("\"CountryCode\""))
            throw new InvalidOperationException("42703: column \"countrycode\" does not exist");
        return Task.FromResult(new DataExplorerResult { Success = true, Source = context.Options.Name, Columns = ["n"], Rows = [new object?[] { 3 }!] });
    }

    public Task<DataExplorerSchemaResult?> GetSchemaAsync(DataExplorerProviderOptions options, CancellationToken cancellationToken = default) =>
        Task.FromResult<DataExplorerSchemaResult?>(new DataExplorerSchemaResult
        {
            Source = options.Name,
            Tables = [new DataExplorerSchemaTable { Name = "public.Users", Type = "table", Columns = [new() { Name = "CountryCode", Type = "text" }] }],
        });
}

public class DataExplorerExecutorNameCaseTests
{
    private static (DataExplorerExecutorService Executor, CaseFoldingDataExplorerProvider Provider, ListAuditStore Audit) Create(
        Action<DataExplorerProviderOptions>? configure = null)
    {
        var provider = new CaseFoldingDataExplorerProvider();
        var options = new DataExplorerProviderOptions { Name = "db", Description = "Test" };
        configure?.Invoke(options);
        var registry = new DataExplorerRegistry();
        registry.Register(provider, options);
        var audit = new ListAuditStore();
        var services = new ServiceCollection().AddSingleton<IAuditStore>(audit).BuildServiceProvider();
        return (new DataExplorerExecutorService(registry, NullLogger<DataExplorerExecutorService>.Instance, services), provider, audit);
    }

    private static DataExplorerExecuteRequest Q(string query) => new() { Source = "db", Query = query };

    [Fact]
    public async Task RunsAgainWithEachNameAsTheSchemaSpellsIt()
    {
        var (executor, provider, audit) = Create();

        var result = await executor.ExecuteAsync(Q("select count(*) from Users where countrycode = 'MD'"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("select count(*) from \"Users\" where \"CountryCode\" = 'MD'", result.ExecutedQuery);
        Assert.Equal(3, provider.Queries.Count);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("select count(*) from Users where countrycode = 'MD'", entry.Properties!["query"]);
        Assert.Equal(result.ExecutedQuery, entry.Properties!["executedQuery"]);
    }

    [Fact]
    public async Task LeavesAQueryThatRanAsSentAlone()
    {
        var (executor, _, _) = Create();
        var result = await executor.ExecuteAsync(Q("select count(*) from \"Users\""));
        Assert.True(result.Success);
        Assert.Null(result.ExecutedQuery);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task OnlyHintsOnAWritableSourceOrWhenTurnedOff(bool readOnly, bool fixNameCase)
    {
        var (executor, provider, _) = Create(o => { o.ReadOnly = readOnly; o.FixNameCase = fixNameCase; });

        var result = await executor.ExecuteAsync(Q("select count(*) from Users"));

        Assert.False(result.Success);
        Assert.EndsWith("Names with capital letters need double quotes: \"Users\"", result.Error);
        Assert.Single(provider.Queries);
    }
}

public class DataExplorerMaskedColumnsTests
{
    private static DataExplorerProviderOptions Opts(L language = L.Sql) => new()
    {
        Name = "db",
        Description = "Test",
        Language = language,
        MaskColumns = ["email", "*phone*"],
        DropColumns = ["password_hash"],
    };

    [Theory]
    [InlineData("SELECT * FROM users")]
    [InlineData("SELECT email FROM users")]
    [InlineData("SELECT DISTINCT u.email, name FROM users u")]
    [InlineData("SELECT TOP 5 email FROM users")]
    [InlineData("SELECT password_hash FROM users;")]
    [InlineData("SELECT * FROM phones")]
    [InlineData("SELECT p.number FROM phones AS p")]
    [InlineData("WITH x AS (SELECT 1 AS a) SELECT a FROM x")]
    public void AllowsMaskedColumnsSelectedByTheirOwnName(string query) => Assert.Null(P.MaskedColumnError(Opts(), query));

    [Theory]
    [InlineData("SELECT email AS e FROM users", "email")]
    [InlineData("SELECT email e FROM users", "email")]
    [InlineData("SELECT upper(email) FROM users", "email")]
    [InlineData("SELECT email || '' FROM users", "email")]
    [InlineData("SELECT id FROM users WHERE email = 'a@x.io'", "email")]
    [InlineData("SELECT * FROM users u WHERE u.email LIKE 'a%'", "email")]
    [InlineData("SELECT * FROM users ORDER BY email", "email")]
    [InlineData("SELECT name AS email, email AS name FROM users", "email")]
    [InlineData("SELECT id FROM t UNION SELECT email FROM users", "email")]
    [InlineData("SELECT (SELECT email FROM users LIMIT 1)", "email")]
    [InlineData("SELECT CASE WHEN home_phone LIKE '5%' THEN 1 END FROM users", "home_phone")]
    [InlineData("SELECT password_hash AS h FROM users", "password_hash")]
    [InlineData("SELECT email # x\nAS e FROM users", "email")]
    [InlineData("SELECT \"id\" FROM \"users\" WHERE \"email\" LIKE '%a%'", "email")]
    [InlineData("SELECT \"id\" FROM \"users\" u WHERE u.\"email\" = 'x'", "email")]
    public void RefusesOtherUsesOfMaskedColumns(string query, string column) =>
        Assert.Equal($"Masked column '{column}' can only be selected by its own name", P.MaskedColumnError(Opts(), query));

    [Theory]
    [InlineData("SELECT u FROM users u", "u")]
    [InlineData("SELECT row_to_json(u) FROM users AS u", "u")]
    [InlineData("SELECT * FROM users WHERE users::text LIKE '%@%'", "users")]
    [InlineData("WITH x AS (SELECT * FROM users) SELECT x FROM x", "x")]
    public void RefusesWholeRows(string query, string name) =>
        Assert.Equal($"'{name}' is a whole row and this source masks columns: select its columns by name", P.MaskedColumnError(Opts(), query));

    [Fact]
    public void RefusesUnicodeEscapedNames() =>
        Assert.Equal("This source masks columns: write names without Unicode escapes (U&\"...\")",
            P.MaskedColumnError(Opts(), "SELECT U&\"\\0065mail\" AS e FROM users"));

    [Fact]
    public void ChecksOnlySqlSourcesThatMask()
    {
        Assert.Null(P.MaskedColumnError(new DataExplorerProviderOptions { Name = "db", Description = "Test" }, "SELECT email AS e FROM users"));
        Assert.Null(P.MaskedColumnError(Opts(L.Shell), "db.users.find({email: 'a'})"));
    }

    [Fact]
    public void WithholdsResultsThatDoNotShowTheColumnUnderItsName()
    {
        Assert.Null(P.MaskedResultError(Opts(), "SELECT email FROM users", ["EMAIL"]));
        Assert.Equal("Masked column 'email' can only be selected by its own name",
            P.MaskedResultError(Opts(), "SELECT email FROM users", ["JSON_F52E"]));
        Assert.Null(P.MaskedResultError(Opts(), "SELECT * FROM users", ["x"]));
    }
}

public class DataExplorerExecutorHardeningTests
{
    private static (DataExplorerExecutorService Executor, RecordingDataExplorerProvider Provider, ListAuditStore Audit, ManualTimeProvider Time)
        Create(Action<DataExplorerProviderOptions>? configure = null, bool readOnlyQueries = false, ListAuditStore? audit = null)
    {
        var provider = new RecordingDataExplorerProvider(readOnlyQueries);
        var options = new DataExplorerProviderOptions { Name = "db", Description = "Test" };
        configure?.Invoke(options);
        var registry = new DataExplorerRegistry();
        registry.Register(provider, options);
        audit ??= new ListAuditStore();
        var services = new ServiceCollection()
            .AddSingleton<IAuditStore>(audit)
            .BuildServiceProvider();
        var time = new ManualTimeProvider();
        var executor = new DataExplorerExecutorService(registry, NullLogger<DataExplorerExecutorService>.Instance, services, time: time);
        return (executor, provider, audit, time);
    }

    private static DataExplorerExecuteRequest Q(string query) => new() { Source = "db", Query = query };

    private static DataExplorerCaller User(string id, params string[] permissions) => new()
    {
        UserId = id,
        UserName = id,
        HasPermission = p => permissions.Contains(p),
    };

    [Fact]
    public async Task RefusesWrites_OnAReadOnlySource_TheDefault_WithoutCallingTheProvider()
    {
        var (executor, provider, _, _) = Create();
        var result = await executor.ExecuteAsync(Q("DELETE FROM users"));
        Assert.False(result.Success);
        Assert.Equal(DataExplorerErrorCodes.ReadOnly, result.ErrorCode);
        Assert.Empty(provider.Queries);
    }

    [Fact]
    public async Task RefusesMaskedColumnsUnderAnotherName_WithoutCallingTheProvider_AndAudits()
    {
        var (executor, provider, audit, _) = Create(o => o.MaskColumns = ["email", "token"]);
        var result = await executor.ExecuteAsync(Q("SELECT email AS e FROM users"));
        Assert.False(result.Success);
        Assert.Equal(DataExplorerErrorCodes.MaskedColumn, result.ErrorCode);
        Assert.Empty(provider.Queries);
        Assert.Equal("Masked column 'email' can only be selected by its own name", Assert.Single(audit.Entries).Properties!["error"]);

        // The provider answers the columns id, email, password: a selected masked column that is not one of them
        // came back under another name, so the rows are withheld
        Assert.True((await executor.ExecuteAsync(Q("SELECT email FROM users"))).Success);
        var withheld = await executor.ExecuteAsync(Q("SELECT token FROM users"));
        Assert.Equal(DataExplorerErrorCodes.MaskedColumn, withheld.ErrorCode);
        Assert.Null(withheld.Rows);
        Assert.Equal(2, provider.Queries.Count);
    }

    [Fact]
    public async Task RunsWrites_WhenReadOnlyIsFalse()
    {
        var (executor, provider, _, _) = Create(o => o.ReadOnly = false);
        var result = await executor.ExecuteAsync(Q("DELETE FROM users"));
        Assert.True(result.Success);
        Assert.Null(result.ErrorCode);
        Assert.Equal(["DELETE FROM users"], provider.Queries);
    }

    [Fact]
    public async Task SkipsTheCheck_ForProvidersThatOnlyRead()
    {
        var (executor, provider, _, _) = Create(o => o.Language = L.Graphql, readOnlyQueries: true);
        Assert.True((await executor.ExecuteAsync(Q("{ users { id } }"))).Success);
        Assert.Single(provider.Queries);
    }

    [Fact]
    public async Task NeedsTheSourcePermissions()
    {
        var (executor, provider, _, _) = Create(o => o.RequiredPermissions = ["data:billing"]);
        var denied = await executor.ExecuteAsync(Q("SELECT 1"), User("u1", "data:query"));
        Assert.Equal(DataExplorerErrorCodes.Forbidden, denied.ErrorCode);
        Assert.Equal("forbidden: missing permission data:billing", denied.Error);
        Assert.Empty(provider.Queries);

        Assert.True((await executor.ExecuteAsync(Q("SELECT 1"), User("u2", "data:query", "data:billing"))).Success);
        // Open server (no permission check) and trusted code
        Assert.True((await executor.ExecuteAsync(Q("SELECT 1"), new DataExplorerCaller())).Success);
        Assert.True((await executor.ExecuteAsync(Q("SELECT 1"))).Success);
    }

    [Fact]
    public async Task LimitsQueriesPerCallerAndWindow()
    {
        var (executor, _, _, time) = Create(o => o.RateLimit = new DataExplorerRateLimit { MaxQueries = 2, WindowSeconds = 60 });
        var alice = User("alice");
        Assert.True((await executor.ExecuteAsync(Q("SELECT 1"), alice)).Success);
        Assert.True((await executor.ExecuteAsync(Q("SELECT 1"), alice)).Success);
        var limited = await executor.ExecuteAsync(Q("SELECT 1"), alice);
        Assert.Equal(DataExplorerErrorCodes.RateLimited, limited.ErrorCode);
        Assert.Equal("Too many queries: at most 2 per 60 s", limited.Error);
        // Another caller has its own window; anonymous callers are counted by address
        Assert.True((await executor.ExecuteAsync(Q("SELECT 1"), User("bob"))).Success);
        Assert.True((await executor.ExecuteAsync(Q("SELECT 1"), new DataExplorerCaller { RemoteAddress = "10.0.0.1" })).Success);

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.True((await executor.ExecuteAsync(Q("SELECT 1"), alice)).Success);
    }

    [Fact]
    public async Task MasksColumns_AndAuditsEveryQuery_RefusalsIncluded()
    {
        var (executor, _, audit, _) = Create(o => { o.MaskColumns = ["email"]; o.DropColumns = ["password"]; });
        var caller = new DataExplorerCaller { UserId = "u1", UserName = "alice", RemoteAddress = "10.0.0.1" };
        var result = await executor.ExecuteAsync(Q("SELECT * FROM users"), caller);
        Assert.Equal(["id", "email"], result.Columns!);
        Assert.Equal("""[[1,"***"],[2,null]]""", JsonSerializer.Serialize(result.Rows));
        Assert.Equal(2, result.RowCount);

        await executor.ExecuteAsync(Q("DROP TABLE users"), caller);

        Assert.Equal(2, audit.Entries.Count);
        var ok = audit.Entries[0];
        Assert.Equal("data_query", ok.Event);
        Assert.Equal("alice", ok.Username);
        Assert.Equal("u1", ok.UserId);
        Assert.Equal("db", ok.Details);
        Assert.Equal("10.0.0.1", ok.Metadata!.RemoteAddress);
        Assert.Equal("db", ok.Properties!["source"]);
        Assert.Equal("SELECT * FROM users", ok.Properties["query"]);
        Assert.Equal(true, ok.Properties["success"]);
        Assert.Equal(2, ok.Properties["rowCount"]);
        Assert.False(ok.Properties.ContainsKey("error"));

        var refused = audit.Entries[1];
        Assert.Equal(false, refused.Properties!["success"]);
        Assert.Contains("Read-only source", (string)refused.Properties["error"]!);
    }

    [Fact]
    public async Task TruncatesLongQueriesInTheAudit_AndSkipsSourcesWithAuditFalse()
    {
        var (executor, _, audit, _) = Create();
        var longQuery = "SELECT " + new string('x', 3000);
        await executor.ExecuteAsync(Q(longQuery));
        var query = (string)audit.Entries.Single().Properties!["query"]!;
        Assert.Equal(DataExplorerExecutorService.AuditQueryMaxLength + 1, query.Length);
        Assert.EndsWith("…", query);
        Assert.Equal("anonymous", audit.Entries[0].Username);

        var (quiet, _, quietAudit, _) = Create(o => o.Audit = false);
        await quiet.ExecuteAsync(Q("SELECT 1"));
        Assert.Empty(quietAudit.Entries);
    }

    [Fact]
    public async Task AFailingAuditLog_DoesNotFailTheQuery()
    {
        var (executor, _, _, _) = Create(audit: new ListAuditStore(fail: true));
        Assert.True((await executor.ExecuteAsync(Q("SELECT 1"))).Success);
    }

    [Fact]
    public void ListsOnlyTheSourcesTheCallerMayUse_WithReadOnlyAndCanExport()
    {
        var registry = new DataExplorerRegistry();
        registry.Register(new RecordingDataExplorerProvider(), new DataExplorerProviderOptions { Name = "open", Description = "" });
        registry.Register(new RecordingDataExplorerProvider(), new DataExplorerProviderOptions
        {
            Name = "billing",
            Description = "",
            RequiredPermissions = ["data:billing"],
            ReadOnly = false,
            ExportPermissions = ["data:export"],
        });

        var analyst = registry.GetSources(User("a", "data:query"));
        Assert.Equal(["open"], analyst.Select(s => s.Name));
        Assert.True(analyst[0].ReadOnly);
        Assert.True(analyst[0].CanExport);

        var billing = registry.GetSources(User("b", "data:billing")).Single(s => s.Name == "billing");
        Assert.False(billing.ReadOnly);
        Assert.False(billing.CanExport);
        Assert.True(registry.GetSources(User("c", "data:billing", "data:export")).Single(s => s.Name == "billing").CanExport);

        // Trusted code and open servers see everything
        Assert.Equal(2, registry.GetSources().Count);
    }
}

public class DataExplorerSqliteReadOnlyTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"qcli-de-{Guid.NewGuid():N}.db");

    public DataExplorerSqliteReadOnlyTests()
    {
        using var connection = new SqliteConnection($"Data Source={_file}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO users (name) VALUES ('alice');";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private static DataExplorerExecutionContext Context(string query, bool readOnly) => new()
    {
        Query = query,
        Options = new DataExplorerProviderOptions { Name = "db", Description = "", ReadOnly = readOnly },
    };

    [Fact]
    public async Task ReadOnlySource_RefusesWrites_InTheConnection()
    {
        var provider = new SqlDataExplorerProvider($"Data Source={_file}");
        var read = await provider.ExecuteAsync(Context("SELECT name FROM users", readOnly: true));
        Assert.Single(read.Rows!);

        var error = await Assert.ThrowsAsync<SqliteException>(() =>
            provider.ExecuteAsync(Context("DELETE FROM users", readOnly: true)));
        Assert.Contains("readonly", error.Message.Replace("-", "").Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

        // A writable source on the same (pooled) database writes
        await provider.ExecuteAsync(Context("DELETE FROM users", readOnly: false));
        Assert.Empty((await provider.ExecuteAsync(Context("SELECT name FROM users", readOnly: true))).Rows!);
    }

    [Fact]
    public async Task ReadOnlySource_LeavesTheHostsPooledConnectionsWritable()
    {
        // The host (an app's EF Core context) uses the same connection string, with pooling
        var connectionString = $"Data Source={_file}";
        var provider = new SqlDataExplorerProvider(connectionString);
        for (var i = 0; i < 3; i++)
        {
            _ = await provider.ExecuteAsync(Context("SELECT name FROM users", readOnly: true));
        }

        for (var i = 0; i < 3; i++)
        {
            await using var host = new SqliteConnection(connectionString);
            await host.OpenAsync();
            await using var insert = host.CreateCommand();
            insert.CommandText = $"INSERT INTO users (name) VALUES ('host{i}')";
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }
    }
}

public class DataExplorerRoutesOnAProtectedServerTests : IAsyncLifetime
{
    private UsersTestHost _h = null!;
    private string _root = null!;

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync(configureCli: cli =>
        {
            cli.AddDataExplorerProvider(new RecordingDataExplorerProvider(), o =>
            {
                o.Name = "app";
                o.Description = "App database";
                o.AllowedObjects = ["users"];
                o.MaskColumns = ["email"];
                o.DropColumns = ["password"];
                o.ExportPermissions = ["data:export"];
                o.RateLimit = new DataExplorerRateLimit { MaxQueries = 3, WindowSeconds = 60 };
            });
            cli.AddDataExplorerProvider(new RecordingDataExplorerProvider(), o =>
            {
                o.Name = "billing";
                o.Description = "Billing";
                o.RequiredPermissions = ["data:billing"];
                o.ReadOnly = false;
            });
            cli.AddMcp();
        });
        _root = await _h.RootTokenAsync();
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private const string Base = "/api/qcli/data-explorer";

    private Task<HttpResponseMessage> ExecuteAsync(string token, string source, string query) =>
        _h.PostAsync($"{Base}/execute", token, new { source, query });

    [Fact]
    public async Task ListsOnlyTheSourcesAUserMayUse_WithReadOnlyAndCanExport()
    {
        var analyst = await _h.UserWithPermissionsAsync(_root, "analyst", "data:query");
        var sources = (await UsersTestHost.ReadJsonAsync(await _h.GetAsync($"{Base}/sources", analyst))).EnumerateArray().ToList();
        var app = Assert.Single(sources);
        Assert.Equal("app", app.GetProperty("name").GetString());
        Assert.True(app.GetProperty("readOnly").GetBoolean());
        Assert.False(app.GetProperty("canExport").GetBoolean());

        var admin = (await UsersTestHost.ReadJsonAsync(await _h.GetAsync($"{Base}/sources", _root))).EnumerateArray().ToList();
        Assert.Equal(2, admin.Count);
        Assert.False(admin.Single(s => s.GetProperty("name").GetString() == "billing").GetProperty("readOnly").GetBoolean());
        Assert.True(admin.Single(s => s.GetProperty("name").GetString() == "app").GetProperty("canExport").GetBoolean());
    }

    [Fact]
    public async Task Answers403_WithoutTheSourcePermissions()
    {
        var analyst = await _h.UserWithPermissionsAsync(_root, "analyst2", "data:query");
        var message = await UsersTestHost.AssertErrorAsync(await ExecuteAsync(analyst, "billing", "SELECT 1"), 403);
        Assert.Equal("forbidden: missing permission data:billing", message);
        await UsersTestHost.AssertErrorAsync(await _h.GetAsync($"{Base}/schema?source=billing", analyst), 403);

        var biller = await _h.UserWithPermissionsAsync(_root, "biller", "data:query", "data:billing");
        Assert.Equal(200, (int)(await ExecuteAsync(biller, "billing", "DELETE FROM invoices")).StatusCode);
    }

    [Fact]
    public async Task RefusesWrites_AndOtherObjects_OnAReadOnlySource_MasksResults_FiltersTheSchema()
    {
        var write = await UsersTestHost.ReadJsonAsync(await ExecuteAsync(_root, "app", "DELETE FROM users"));
        Assert.False(write.GetProperty("success").GetBoolean());
        Assert.Equal("read_only", write.GetProperty("errorCode").GetString());

        var other = await UsersTestHost.ReadJsonAsync(await ExecuteAsync(_root, "app", "SELECT * FROM secrets"));
        Assert.Equal("object_not_allowed", other.GetProperty("errorCode").GetString());

        var ok = await UsersTestHost.ReadJsonAsync(await ExecuteAsync(_root, "app", "SELECT * FROM users"));
        Assert.True(ok.GetProperty("success").GetBoolean());
        Assert.False(ok.TryGetProperty("errorCode", out _));
        Assert.Equal("""["id","email"]""", ok.GetProperty("columns").GetRawText());
        Assert.Equal("""[[1,"***"],[2,null]]""", ok.GetProperty("rows").GetRawText());

        var schema = await UsersTestHost.ReadJsonAsync(await _h.GetAsync($"{Base}/schema?source=app", _root));
        var table = Assert.Single(schema.GetProperty("tables").EnumerateArray());
        Assert.Equal("users", table.GetProperty("name").GetString());
        Assert.Equal(["id"], table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Answers429_OverTheRateLimit()
    {
        var user = await _h.UserWithPermissionsAsync(_root, "busy", "data:query");
        for (var i = 0; i < 3; i++)
            Assert.Equal(200, (int)(await ExecuteAsync(user, "app", "SELECT * FROM users")).StatusCode);
        var message = await UsersTestHost.AssertErrorAsync(await ExecuteAsync(user, "app", "SELECT * FROM users"), 429);
        Assert.Equal("Too many queries: at most 3 per 60 s", message);
    }

    [Fact]
    public async Task WritesDataQueryEntries_ToTheAuditLog()
    {
        var user = await _h.UserWithPermissionsAsync(_root, "audited", "data:query");
        await ExecuteAsync(user, "app", "SELECT * FROM users");
        await ExecuteAsync(user, "app", "DROP TABLE users");

        var entries = (await _h.Service<IAuditStore>().GetEntriesAsync())
            .Where(e => e.Event == "data_query" && e.Username == "audited")
            .OrderBy(e => e.Properties!["success"] is true ? 0 : 1)
            .ToList();
        Assert.Equal(2, entries.Count);
        Assert.NotNull(entries[0].UserId);
        Assert.Equal("SELECT * FROM users", entries[0].Properties!["query"]);
        Assert.Contains("Read-only source", (string)entries[1].Properties!["error"]!);
    }

    [Fact]
    public async Task McpTools_ApplyThePermissions_ReadOnlyRules_AndSchemaFilter()
    {
        var analyst = await _h.UserWithPermissionsAsync(_root, "mcp-analyst", "commands:execute", "data:query");

        var sources = await Mcp.CallAsync(_h.Client, "data_sources", null, analyst);
        Assert.Contains("app", Mcp.Text(sources));
        Assert.DoesNotContain("billing", Mcp.Text(sources));

        var schema = await Mcp.CallAsync(_h.Client, "data_schema", new { source = "billing" }, analyst);
        Assert.True(schema.GetProperty("isError").GetBoolean());
        Assert.Contains("forbidden", Mcp.Text(schema));
        Assert.DoesNotContain("secrets", Mcp.Text(await Mcp.CallAsync(_h.Client, "data_schema", new { source = "app" }, analyst)));

        var forbidden = await Mcp.CallAsync(_h.Client, "data_query", new { source = "billing", query = "SELECT 1" }, analyst);
        Assert.True(forbidden.GetProperty("isError").GetBoolean());

        var write = await Mcp.CallAsync(_h.Client, "data_query", new { source = "app", query = "DELETE FROM users" }, analyst);
        Assert.True(write.GetProperty("isError").GetBoolean());
        Assert.Contains("Read-only source", Mcp.Text(write));

        var read = await Mcp.CallAsync(_h.Client, "data_query", new { source = "app", query = "SELECT * FROM users" }, analyst);
        Assert.False(read.GetProperty("isError").GetBoolean());
        Assert.Contains("***", Mcp.Text(read));
        Assert.DoesNotContain("a@x.io", Mcp.Text(read));
    }
}

/// <summary>A real PostgreSQL server (connection string in <c>QCLI_TEST_POSTGRES</c>; skipped without it).</summary>
public class PostgresReadOnlyTests
{
    private static async Task<DataExplorerResult?> RunAsync(string query, bool readOnly)
    {
        var connectionString = Environment.GetEnvironmentVariable("QCLI_TEST_POSTGRES");
        if (string.IsNullOrEmpty(connectionString)) return null;
        var options = new DataExplorerProviderOptions { Name = "pg", Description = "Test", ReadOnly = readOnly };
        try
        {
            return await new PostgresDataExplorerProvider(connectionString).ExecuteAsync(new DataExplorerExecutionContext
            {
                Query = query,
                Options = options,
            });
        }
        catch (Exception ex)
        {
            return new DataExplorerResult { Success = false, Source = "pg", Error = ex.Message };
        }
    }

    [Fact]
    public async Task ReadOnlySource_RunsOneStatementOnly()
    {
        var table = "qcli_ro_" + Guid.NewGuid().ToString("N")[..8];
        if (await RunAsync($"CREATE TABLE {table} (id int)", readOnly: false) == null) return;
        try
        {
            // The server's text checks are bypassed here: the provider alone must not run the second statement
            var batch = await RunAsync($"SELECT 1; COMMIT; INSERT INTO {table} VALUES (1)", readOnly: true);
            Assert.False(batch!.Success);
            var write = await RunAsync($"INSERT INTO {table} VALUES (2)", readOnly: true);
            Assert.False(write!.Success);
            Assert.True((await RunAsync("SELECT 1 AS one;", readOnly: true))!.Success);
            var rows = await RunAsync($"SELECT count(*) AS n FROM {table}", readOnly: true);
            Assert.Equal(0L, Convert.ToInt64(((object[])rows!.Rows![0])[0]));
        }
        finally
        {
            await RunAsync($"DROP TABLE {table}", readOnly: false);
        }
    }
}
