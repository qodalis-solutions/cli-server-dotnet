using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Qodalis.Cli.Abstractions.DataExplorer;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Qodalis.Cli.Plugin.DataExplorer.Mongo;

public class MongoDataExplorerProvider : IDataExplorerProvider
{
    private readonly string _connectionString;
    private readonly string _database;

    public MongoDataExplorerProvider(string connectionString, string database)
    {
        _connectionString = connectionString;
        _database = database;
    }

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

            // Convenience commands
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

            // Parse db.collection.operation(...)
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

    private record struct ParsedQuery(string Collection, string Operation, List<string> Args);

    private static ParsedQuery? ParseQuery(string query)
    {
        var match = Regex.Match(query, @"^db\.(\w+)\.(\w+)\(([\s\S]*)\)$");
        if (!match.Success) return null;

        var collection = match.Groups[1].Value;
        var operation = match.Groups[2].Value;
        var argsStr = match.Groups[3].Value.Trim();

        if (string.IsNullOrEmpty(argsStr))
            return new ParsedQuery(collection, operation, new List<string>());

        // Split top-level arguments by comma (respecting nesting)
        var args = SplitArguments(argsStr);
        return new ParsedQuery(collection, operation, args);
    }

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

    private static Dictionary<string, object> BsonDocToDict(BsonDocument doc)
    {
        var dict = new Dictionary<string, object>();
        foreach (var element in doc)
        {
            dict[element.Name] = BsonValueToObject(element.Value);
        }
        return dict;
    }

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
