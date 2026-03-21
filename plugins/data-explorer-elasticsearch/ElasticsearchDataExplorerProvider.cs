using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.DataExplorer;

namespace Qodalis.Cli.Plugin.DataExplorer.Elasticsearch;

public class ElasticsearchDataExplorerProvider : IDataExplorerProvider
{
    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ElasticsearchDataExplorerProvider(string node)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(node.TrimEnd('/') + "/")
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

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

        try
        {
            var (method, path, body) = ParseQuery(context.Query);

            // _cat endpoints: append ?format=json if not already present
            if (path.Contains("/_cat/") || path.StartsWith("_cat/"))
            {
                if (!path.Contains("format="))
                    path = path.Contains('?') ? path + "&format=json" : path + "?format=json";
            }

            var responseJson = await SendRequestAsync(method, path, body, cancellationToken);

            if (responseJson == null)
            {
                result.Success = false;
                result.Error = "Empty response from Elasticsearch.";
                return result;
            }

            // Determine how to shape the result
            if (path.Contains("/_search") || path.EndsWith("/_search"))
            {
                FlattenSearchHits(responseJson, result);
            }
            else if (path.Contains("/_cat/") || path.StartsWith("_cat/"))
            {
                FlattenArrayResponse(responseJson, result);
            }
            else
            {
                // Raw JSON result
                result.Columns = new List<string> { "response" };
                result.Rows = new List<object> { new object[] { responseJson.ToJsonString() } };
                result.RowCount = 1;
                result.DefaultOutputFormat = DataExplorerOutputFormat.Json;
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    public async Task<DataExplorerSchemaResult?> GetSchemaAsync(
        DataExplorerProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Get index list
            var indicesJson = await SendRequestAsync(
                HttpMethod.Get, "_cat/indices?format=json&h=index,health,status,pri,rep,docs.count,store.size",
                null, cancellationToken);

            var tables = new List<DataExplorerSchemaTable>();

            if (indicesJson is JsonArray indicesArray)
            {
                foreach (var indexEntry in indicesArray)
                {
                    if (indexEntry == null) continue;

                    var indexName = indexEntry["index"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(indexName) || indexName.StartsWith('.'))
                        continue;

                    var columns = new List<DataExplorerSchemaColumn>
                    {
                        new() { Name = "_id", Type = "keyword", Nullable = false, PrimaryKey = true },
                        new() { Name = "_index", Type = "keyword", Nullable = false, PrimaryKey = false }
                    };

                    // Get mapping for each index
                    try
                    {
                        var mappingJson = await SendRequestAsync(
                            HttpMethod.Get, $"{indexName}/_mapping", null, cancellationToken);

                        if (mappingJson is JsonObject mappingObj)
                        {
                            // mappingObj structure: { "<indexName>": { "mappings": { "properties": { ... } } } }
                            var indexMappingNode = mappingObj[indexName];
                            var propertiesNode = indexMappingNode?["mappings"]?["properties"];
                            if (propertiesNode is JsonObject properties)
                            {
                                ExtractMappingColumns(properties, string.Empty, columns);
                            }
                        }
                    }
                    catch
                    {
                        // If mapping fails, still add the index with minimal columns
                    }

                    tables.Add(new DataExplorerSchemaTable
                    {
                        Name = indexName,
                        Type = "index",
                        Columns = columns
                    });
                }
            }

            return new DataExplorerSchemaResult
            {
                Source = options.Name,
                Tables = tables
            };
        }
        catch
        {
            return new DataExplorerSchemaResult { Source = options.Name };
        }
    }

    // Parse query: first non-empty line = "[VERB] /path", rest = JSON body
    private static (HttpMethod Method, string Path, string? Body) ParseQuery(string query)
    {
        var lines = query.Split('\n');
        var firstLine = string.Empty;
        var bodyStartIndex = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                firstLine = trimmed;
                bodyStartIndex = i + 1;
                break;
            }
        }

        HttpMethod method;
        string path;

        var spaceIdx = firstLine.IndexOf(' ');
        if (spaceIdx > 0)
        {
            var verbStr = firstLine[..spaceIdx].Trim().ToUpperInvariant();
            path = firstLine[(spaceIdx + 1)..].Trim().TrimStart('/');
            method = verbStr switch
            {
                "POST" => HttpMethod.Post,
                "PUT" => HttpMethod.Put,
                "DELETE" => HttpMethod.Delete,
                "HEAD" => HttpMethod.Head,
                "PATCH" => HttpMethod.Patch,
                _ => HttpMethod.Get
            };
        }
        else
        {
            // No verb — bare path, default to GET
            method = HttpMethod.Get;
            path = firstLine.TrimStart('/');
        }

        var bodyLines = lines.Skip(bodyStartIndex).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        var body = bodyLines.Length > 0 ? string.Join('\n', bodyLines) : null;

        return (method, path, body);
    }

    private async Task<JsonNode?> SendRequestAsync(
        HttpMethod method, string path, string? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, path);

        if (!string.IsNullOrWhiteSpace(body))
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(content))
            return null;

        return JsonNode.Parse(content);
    }

    private static void FlattenSearchHits(JsonNode responseJson, DataExplorerResult result)
    {
        var hitsNode = responseJson["hits"]?["hits"];
        if (hitsNode is not JsonArray hitsArray || hitsArray.Count == 0)
        {
            result.Columns = new List<string> { "response" };
            result.Rows = new List<object> { new object[] { responseJson.ToJsonString() } };
            result.RowCount = 1;
            result.DefaultOutputFormat = DataExplorerOutputFormat.Json;
            return;
        }

        // Collect all field keys across all hits
        var columnSet = new LinkedList<string>();
        var columnIndex = new Dictionary<string, int>();

        // Always include meta fields first
        foreach (var meta in new[] { "_index", "_id", "_score" })
        {
            columnIndex[meta] = columnSet.Count;
            columnSet.AddLast(meta);
        }

        foreach (var hit in hitsArray)
        {
            var source = hit?["_source"];
            if (source is not JsonObject sourceObj) continue;

            foreach (var prop in sourceObj)
            {
                if (!columnIndex.ContainsKey(prop.Key))
                {
                    columnIndex[prop.Key] = columnSet.Count;
                    columnSet.AddLast(prop.Key);
                }
            }
        }

        var columns = columnSet.ToList();
        result.Columns = columns;

        var rows = new List<object>();
        foreach (var hit in hitsArray)
        {
            if (hit == null) continue;
            var row = new object?[columns.Count];

            row[columnIndex["_index"]] = hit["_index"]?.GetValue<string>();
            row[columnIndex["_id"]] = hit["_id"]?.GetValue<string>();
            row[columnIndex["_score"]] = hit["_score"]?.GetValue<double?>() ?? (object?)null;

            var source = hit["_source"];
            if (source is JsonObject sourceObj)
            {
                foreach (var prop in sourceObj)
                {
                    if (columnIndex.TryGetValue(prop.Key, out var colIdx))
                    {
                        row[colIdx] = prop.Value?.GetValue<object>() ?? prop.Value?.ToJsonString();
                    }
                }
            }

            rows.Add(row!);
        }

        result.Rows = rows;
        result.RowCount = rows.Count;
    }

    private static void FlattenArrayResponse(JsonNode responseJson, DataExplorerResult result)
    {
        if (responseJson is not JsonArray arr || arr.Count == 0)
        {
            result.Columns = new List<string> { "response" };
            result.Rows = new List<object> { new object[] { responseJson.ToJsonString() } };
            result.RowCount = 1;
            return;
        }

        // Collect all keys
        var columnSet = new LinkedList<string>();
        var columnIndex = new Dictionary<string, int>();

        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            foreach (var prop in obj)
            {
                if (!columnIndex.ContainsKey(prop.Key))
                {
                    columnIndex[prop.Key] = columnSet.Count;
                    columnSet.AddLast(prop.Key);
                }
            }
        }

        var columns = columnSet.ToList();
        result.Columns = columns;

        var rows = new List<object>();
        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            var row = new object?[columns.Count];
            foreach (var prop in obj)
            {
                if (columnIndex.TryGetValue(prop.Key, out var colIdx))
                {
                    row[colIdx] = prop.Value?.GetValue<object>() ?? prop.Value?.ToJsonString();
                }
            }
            rows.Add(row!);
        }

        result.Rows = rows;
        result.RowCount = rows.Count;
    }

    private static void ExtractMappingColumns(
        JsonObject properties, string prefix, List<DataExplorerSchemaColumn> columns)
    {
        foreach (var prop in properties)
        {
            var fieldName = string.IsNullOrEmpty(prefix) ? prop.Key : $"{prefix}.{prop.Key}";
            var fieldDef = prop.Value as JsonObject;
            var fieldType = fieldDef?["type"]?.GetValue<string>() ?? "object";

            // Recurse into nested object/nested type properties
            var nestedProps = fieldDef?["properties"] as JsonObject;
            if (nestedProps != null)
            {
                ExtractMappingColumns(nestedProps, fieldName, columns);
            }
            else
            {
                columns.Add(new DataExplorerSchemaColumn
                {
                    Name = fieldName,
                    Type = fieldType,
                    Nullable = true,
                    PrimaryKey = false
                });
            }
        }
    }
}
