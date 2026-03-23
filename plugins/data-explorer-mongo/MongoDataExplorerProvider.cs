using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Qodalis.Cli.Abstractions.DataExplorer;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Qodalis.Cli.Plugin.DataExplorer.Mongo;

/// <summary>
/// Data explorer provider for MongoDB databases using the official MongoDB .NET driver.
/// Supports shell-style queries such as <c>db.collection.find({})</c>, aggregation pipelines,
/// CRUD operations, and convenience commands like <c>show collections</c>.
/// </summary>
public class MongoDataExplorerProvider : IDataExplorerProvider
{
    private readonly string _connectionString;
    private readonly string _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoDataExplorerProvider"/> class.
    /// </summary>
    /// <param name="connectionString">The MongoDB connection string.</param>
    /// <param name="database">The name of the database to operate on.</param>
    public MongoDataExplorerProvider(string connectionString, string database)
    {
        _connectionString = connectionString;
        _database = database;
    }

    /// <summary>
    /// Retrieves the database schema by listing all collections and sampling one document from each to infer field structure.
    /// </summary>
    /// <param name="options">The provider options containing the data source name.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The schema result containing collections and their inferred columns, or <c>null</c> on failure.</returns>
    public async Task<DataExplorerSchemaResult?> GetSchemaAsync(
        DataExplorerProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        var client = new MongoClient(_connectionString);
        var db = client.GetDatabase(_database);

        var collectionNames = await (await db.ListCollectionNamesAsync(cancellationToken: cancellationToken))
            .ToListAsync(cancellationToken);

        var tables = new List<DataExplorerSchemaTable>();
        foreach (var collName in collectionNames)
        {
            var collection = db.GetCollection<BsonDocument>(collName);
            var sample = await collection.Find(new BsonDocument()).Limit(1).FirstOrDefaultAsync(cancellationToken);

            var columns = new List<DataExplorerSchemaColumn>();
            if (sample != null)
            {
                foreach (var element in sample)
                {
                    columns.Add(new DataExplorerSchemaColumn
                    {
                        Name = element.Name,
                        Type = element.Value.BsonType.ToString().ToLowerInvariant(),
                        Nullable = true,
                        PrimaryKey = element.Name == "_id"
                    });
                }
            }

            tables.Add(new DataExplorerSchemaTable
            {
                Name = collName,
                Type = "collection",
                Columns = columns
            });
        }

        return new DataExplorerSchemaResult
        {
            Source = options.Name,
            Tables = tables
        };
    }

    /// <summary>
    /// Executes a MongoDB shell-style query and returns the results.
    /// Supports operations: find, findOne, aggregate, insertOne, insertMany,
    /// updateOne, updateMany, deleteOne, deleteMany, countDocuments,
    /// as well as <c>show collections</c> and <c>show dbs</c>.
    /// </summary>
    /// <param name="context">The execution context containing the query string and options.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The query result containing rows and metadata.</returns>
    public async Task<DataExplorerResult> ExecuteAsync(
        DataExplorerExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var client = new MongoClient(_connectionString);
        var db = client.GetDatabase(_database);

        try
        {
            var query = context.Query.Trim();

            if (query.Equals("show collections", StringComparison.OrdinalIgnoreCase))
            {
                var collections = await (await db.ListCollectionNamesAsync(cancellationToken: cancellationToken)).ToListAsync(cancellationToken);
                var rows = collections.Select(c => (object)new Dictionary<string, object> { ["name"] = c }).ToList();
                return SuccessResult(context, sw, null, rows, rows.Count);
            }

            if (query.Equals("show dbs", StringComparison.OrdinalIgnoreCase) ||
                query.Equals("show databases", StringComparison.OrdinalIgnoreCase))
            {
                var databases = await (await client.ListDatabasesAsync(cancellationToken)).ToListAsync(cancellationToken);
                var rows = databases.Select(d => (object)BsonDocToDict(d)).ToList();
                return SuccessResult(context, sw, null, rows, rows.Count);
            }

            var parsed = ParseQuery(query);
            if (parsed == null)
            {
                return ErrorResult(context, sw,
                    "Invalid query syntax. Use: db.collection.find({...}), db.collection.aggregate([...]), show collections, show dbs");
            }

            var collection = db.GetCollection<BsonDocument>(parsed.Value.Collection);

            switch (parsed.Value.Operation.ToLowerInvariant())
            {
                case "find":
                {
                    var filter = parsed.Value.Args.Count > 0 ? BsonDocument.Parse(parsed.Value.Args[0]) : new BsonDocument();
                    var projection = parsed.Value.Args.Count > 1 ? BsonDocument.Parse(parsed.Value.Args[1]) : null;
                    var findOptions = new FindOptions<BsonDocument> { };
                    if (projection != null) findOptions.Projection = projection;

                    var maxRows = context.Options.MaxRows > 0 ? context.Options.MaxRows : 1000;
                    var cursor = await collection.FindAsync(filter, findOptions, cancellationToken);
                    var docs = new List<BsonDocument>();
                    var truncated = false;
                    while (await cursor.MoveNextAsync(cancellationToken))
                    {
                        foreach (var doc in cursor.Current)
                        {
                            if (docs.Count >= maxRows)
                            {
                                truncated = true;
                                break;
                            }
                            docs.Add(doc);
                        }
                        if (truncated) break;
                    }
                    var rows = docs.Select(d => (object)BsonDocToDict(d)).ToList();
                    return SuccessResult(context, sw, null, rows, rows.Count, truncated);
                }
                case "findone":
                {
                    var filter = parsed.Value.Args.Count > 0 ? BsonDocument.Parse(parsed.Value.Args[0]) : new BsonDocument();
                    var doc = await collection.Find(filter).FirstOrDefaultAsync(cancellationToken);
                    var rows = doc != null
                        ? new List<object> { BsonDocToDict(doc) }
                        : new List<object>();
                    return SuccessResult(context, sw, null, rows, rows.Count);
                }
                case "aggregate":
                {
                    var pipelineJson = parsed.Value.Args.Count > 0 ? parsed.Value.Args[0] : "[]";
                    var pipeline = BsonSerializer.Deserialize<BsonArray>(pipelineJson)
                        .Select(s => s.AsBsonDocument)
                        .ToList();
                    var docs = await collection.Aggregate<BsonDocument>(
                        PipelineDefinition<BsonDocument, BsonDocument>.Create(pipeline.Select(p =>
                            (PipelineStageDefinition<BsonDocument, BsonDocument>)new BsonDocumentPipelineStageDefinition<BsonDocument, BsonDocument>(p)).ToArray()),
                        cancellationToken: cancellationToken
                    ).ToListAsync(cancellationToken);
                    var rows = docs.Select(d => (object)BsonDocToDict(d)).ToList();
                    return SuccessResult(context, sw, null, rows, rows.Count);
                }
                case "insertone":
                {
                    var doc = BsonDocument.Parse(parsed.Value.Args.Count > 0 ? parsed.Value.Args[0] : "{}");
                    await collection.InsertOneAsync(doc, cancellationToken: cancellationToken);
                    var rows = new List<object> { new Dictionary<string, object> { ["acknowledged"] = true, ["insertedId"] = doc["_id"].ToString()! } };
                    return SuccessResult(context, sw, null, rows, 1);
                }
                case "insertmany":
                {
                    var docsJson = parsed.Value.Args.Count > 0 ? parsed.Value.Args[0] : "[]";
                    var docs = BsonSerializer.Deserialize<BsonArray>(docsJson).Select(b => b.AsBsonDocument).ToList();
                    await collection.InsertManyAsync(docs, cancellationToken: cancellationToken);
                    var rows = new List<object> { new Dictionary<string, object> { ["acknowledged"] = true, ["insertedCount"] = docs.Count } };
                    return SuccessResult(context, sw, null, rows, 1);
                }
                case "updateone":
                {
                    var filter = BsonDocument.Parse(parsed.Value.Args.Count > 0 ? parsed.Value.Args[0] : "{}");
                    var update = BsonDocument.Parse(parsed.Value.Args.Count > 1 ? parsed.Value.Args[1] : "{}");
                    var result = await collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
                    var rows = new List<object> { new Dictionary<string, object> { ["acknowledged"] = result.IsAcknowledged, ["matchedCount"] = result.MatchedCount, ["modifiedCount"] = result.ModifiedCount } };
                    return SuccessResult(context, sw, null, rows, 1);
                }
                case "updatemany":
                {
                    var filter = BsonDocument.Parse(parsed.Value.Args.Count > 0 ? parsed.Value.Args[0] : "{}");
                    var update = BsonDocument.Parse(parsed.Value.Args.Count > 1 ? parsed.Value.Args[1] : "{}");
                    var result = await collection.UpdateManyAsync(filter, update, cancellationToken: cancellationToken);
                    var rows = new List<object> { new Dictionary<string, object> { ["acknowledged"] = result.IsAcknowledged, ["matchedCount"] = result.MatchedCount, ["modifiedCount"] = result.ModifiedCount } };
                    return SuccessResult(context, sw, null, rows, 1);
                }
                case "deleteone":
                {
                    var filter = BsonDocument.Parse(parsed.Value.Args.Count > 0 ? parsed.Value.Args[0] : "{}");
                    var result = await collection.DeleteOneAsync(filter, cancellationToken);
                    var rows = new List<object> { new Dictionary<string, object> { ["acknowledged"] = result.IsAcknowledged, ["deletedCount"] = result.DeletedCount } };
                    return SuccessResult(context, sw, null, rows, 1);
                }
                case "deletemany":
                {
                    var filter = BsonDocument.Parse(parsed.Value.Args.Count > 0 ? parsed.Value.Args[0] : "{}");
                    var result = await collection.DeleteManyAsync(filter, cancellationToken);
                    var rows = new List<object> { new Dictionary<string, object> { ["acknowledged"] = result.IsAcknowledged, ["deletedCount"] = result.DeletedCount } };
                    return SuccessResult(context, sw, null, rows, 1);
                }
                case "countdocuments":
                {
                    var filter = parsed.Value.Args.Count > 0 ? BsonDocument.Parse(parsed.Value.Args[0]) : new BsonDocument();
                    var count = await collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
                    return SuccessResult(context, sw, new List<string> { "count" }, new List<object> { new object[] { count } }, 1);
                }
                default:
                    return ErrorResult(context, sw,
                        $"Unsupported operation: {parsed.Value.Operation}. Supported: find, findOne, aggregate, insertOne, insertMany, updateOne, updateMany, deleteOne, deleteMany, countDocuments");
            }
        }
        catch (Exception ex)
        {
            return ErrorResult(context, sw, ex.Message);
        }
    }

    /// <summary>
    /// Represents a parsed MongoDB shell-style query with collection name, operation, and arguments.
    /// </summary>
    private record struct ParsedQuery(string Collection, string Operation, List<string> Args);

    /// <summary>
    /// Parses a MongoDB shell-style query string (e.g., <c>db.collection.find({...})</c>) into its components.
    /// </summary>
    private static ParsedQuery? ParseQuery(string query)
    {
        var match = Regex.Match(query, @"^db\.(\w+)\.(\w+)\(([\s\S]*)\)$");
        if (!match.Success) return null;

        var collection = match.Groups[1].Value;
        var operation = match.Groups[2].Value;
        var argsStr = match.Groups[3].Value.Trim();

        if (string.IsNullOrEmpty(argsStr))
            return new ParsedQuery(collection, operation, new List<string>());

        var args = SplitArguments(argsStr);
        return new ParsedQuery(collection, operation, args);
    }

    /// <summary>
    /// Splits a comma-separated argument string while respecting nested braces and brackets.
    /// </summary>
    private static List<string> SplitArguments(string argsStr)
    {
        var args = new List<string>();
        var depth = 0;
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < argsStr.Length; i++)
        {
            var c = argsStr[i];
            if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']') depth--;
            else if (c == ',' && depth == 0)
            {
                args.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(c);
        }

        if (current.Length > 0)
            args.Add(current.ToString().Trim());

        return args;
    }

    /// <summary>
    /// Converts a <see cref="BsonDocument"/> to a dictionary of string keys and .NET objects.
    /// </summary>
    private static Dictionary<string, object> BsonDocToDict(BsonDocument doc)
    {
        var dict = new Dictionary<string, object>();
        foreach (var element in doc)
        {
            dict[element.Name] = BsonValueToObject(element.Value);
        }
        return dict;
    }

    /// <summary>
    /// Converts a <see cref="BsonValue"/> to its corresponding .NET object representation.
    /// </summary>
    private static object BsonValueToObject(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.ObjectId => value.AsObjectId.ToString(),
            BsonType.String => value.AsString,
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => value.AsInt64,
            BsonType.Double => value.AsDouble,
            BsonType.Boolean => value.AsBoolean,
            BsonType.DateTime => value.ToUniversalTime().ToString("o"),
            BsonType.Null => null!,
            BsonType.Array => value.AsBsonArray.Select(BsonValueToObject).ToList(),
            BsonType.Document => BsonDocToDict(value.AsBsonDocument),
            _ => value.ToString()!,
        };
    }

    /// <summary>
    /// Creates a successful <see cref="DataExplorerResult"/> with the provided data.
    /// </summary>
    private static DataExplorerResult SuccessResult(
        DataExplorerExecutionContext context, Stopwatch sw,
        List<string>? columns, List<object> rows, int rowCount, bool truncated = false)
    {
        return new DataExplorerResult
        {
            Success = true,
            Source = context.Options.Name,
            Language = context.Options.Language,
            DefaultOutputFormat = context.Options.DefaultOutputFormat,
            ExecutionTime = sw.ElapsedMilliseconds,
            Columns = columns,
            Rows = rows,
            RowCount = rowCount,
            Truncated = truncated,
            Error = null,
        };
    }

    /// <summary>
    /// Creates a failed <see cref="DataExplorerResult"/> with the provided error message.
    /// </summary>
    private static DataExplorerResult ErrorResult(
        DataExplorerExecutionContext context, Stopwatch sw, string error)
    {
        return new DataExplorerResult
        {
            Success = false,
            Source = context.Options.Name,
            Language = context.Options.Language,
            DefaultOutputFormat = context.Options.DefaultOutputFormat,
            ExecutionTime = sw.ElapsedMilliseconds,
            Columns = null,
            Rows = null,
            RowCount = 0,
            Truncated = false,
            Error = error,
        };
    }
}
