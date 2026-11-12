using System.Text.Json.Serialization;
using Qodalis.Cli.Abstractions.Helpers;

namespace Qodalis.Cli.Abstractions.DataExplorer;

/// <summary>
/// The query language used by a data explorer provider.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter))]
public enum DataExplorerLanguage { Sql, Json, Shell, Graphql, Redis, Elasticsearch }

/// <summary>
/// The output format for data explorer query results.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter))]
public enum DataExplorerOutputFormat { Table, Json, Csv, Raw }

/// <summary>
/// Represents a predefined query template for a data explorer provider.
/// </summary>
public class DataExplorerTemplate
{
    /// <summary>Gets or sets the template display name.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the query string for this template.</summary>
    public required string Query { get; set; }

    /// <summary>Gets or sets an optional description of what this template does.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the default parameter values for this template.</summary>
    public Dictionary<string, object>? Parameters { get; set; }
}

/// <summary>
/// Describes a parameter accepted by a data explorer query.
/// </summary>
public class DataExplorerParameterDescriptor
{
    /// <summary>Gets or sets the parameter name.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the parameter description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets whether this parameter is required.</summary>
    public bool Required { get; set; }

    /// <summary>Gets or sets the default value when the parameter is not provided.</summary>
    public object? DefaultValue { get; set; }
}

/// <summary>
/// Configuration options for a data explorer provider, including query language, timeouts, and templates.
/// </summary>
public class DataExplorerProviderOptions
{
    /// <summary>Gets or sets the unique name identifying this data source.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets a human-readable description of this data source.</summary>
    public required string Description { get; set; }

    /// <summary>Gets or sets the query language used by this provider.</summary>
    public DataExplorerLanguage Language { get; set; } = DataExplorerLanguage.Sql;

    /// <summary>Gets or sets the default output format for query results.</summary>
    public DataExplorerOutputFormat DefaultOutputFormat { get; set; } = DataExplorerOutputFormat.Table;

    /// <summary>Gets or sets the parameter descriptors accepted by this provider.</summary>
    public List<DataExplorerParameterDescriptor> Parameters { get; set; } = [];

    /// <summary>Gets or sets the predefined query templates.</summary>
    public List<DataExplorerTemplate> Templates { get; set; } = [];

    /// <summary>Gets or sets the query timeout in milliseconds. Default is 30000.</summary>
    public int Timeout { get; set; } = 30000;

    /// <summary>Gets or sets the maximum number of rows returned. Default is 1000.</summary>
    public int MaxRows { get; set; } = 1000;

    /// <summary>
    /// Permissions the caller must hold (all of them, besides <c>data:query</c>) to see and use the source.
    /// Ignored on an open server (no authentication provider).
    /// </summary>
    public List<string> RequiredPermissions { get; set; } = [];

    /// <summary>
    /// Queries may only read (default <c>true</c>): the server checks the query by language before the provider
    /// runs, and SQL providers run it in a read-only transaction. Set <c>false</c> for a source that must write.
    /// </summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>
    /// Glob patterns (<c>*</c> matches anything, case-insensitive) of the tables (<c>schema.table</c> or
    /// <c>table</c>), collections, Redis keys or Elasticsearch indices the source shows and accepts. Empty = all.
    /// Best effort: the database account's own permissions are the real boundary.
    /// </summary>
    public List<string> AllowedObjects { get; set; } = [];

    /// <summary>Glob patterns of column/field names whose values are replaced by <c>***</c> in results (<c>null</c> stays <c>null</c>).</summary>
    public List<string> MaskColumns { get; set; } = [];

    /// <summary>Glob patterns of columns/fields removed from results and from the schema.</summary>
    public List<string> DropColumns { get; set; } = [];

    /// <summary>Writes every execute to the audit log (event <c>data_query</c>) when the server has one. Default <c>true</c>.</summary>
    public bool Audit { get; set; } = true;

    /// <summary>At most <see cref="DataExplorerRateLimit.MaxQueries"/> executes per caller per window on this source; <c>null</c> = no limit.</summary>
    public DataExplorerRateLimit? RateLimit { get; set; }

    /// <summary>
    /// Permissions needed to export results (<c>json</c>/<c>csv</c> output in the web CLI); reported as
    /// <see cref="DataExplorerSourceInfo.CanExport"/>. Advisory: the table output shows the same rows.
    /// </summary>
    public List<string> ExportPermissions { get; set; } = [];
}

/// <summary>
/// Rate limit of a data source: at most <see cref="MaxQueries"/> executes per caller (user id, or client address
/// when anonymous) per <see cref="WindowSeconds"/>.
/// </summary>
public class DataExplorerRateLimit
{
    /// <summary>Gets or sets the most executes allowed in a window.</summary>
    public int MaxQueries { get; set; }

    /// <summary>Gets or sets the window length in seconds.</summary>
    public int WindowSeconds { get; set; }
}

/// <summary>Why the server refused a data explorer query (<see cref="DataExplorerResult.ErrorCode"/>).</summary>
public static class DataExplorerErrorCodes
{
    /// <summary>The caller lacks a permission of the source (HTTP 403).</summary>
    public const string Forbidden = "forbidden";

    /// <summary>The caller is over the source's rate limit (HTTP 429).</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>The source is read-only and the query may write.</summary>
    public const string ReadOnly = "read_only";

    /// <summary>The query names an object outside the source's allowed objects.</summary>
    public const string ObjectNotAllowed = "object_not_allowed";

    /// <summary>No source has that name.</summary>
    public const string UnknownSource = "unknown_source";
}

/// <summary>
/// Who runs a data explorer request, as the server knows it. <see cref="HasPermission"/> is <c>null</c> on an open
/// server (no authentication provider): permission options are then not enforced.
/// </summary>
public class DataExplorerCaller
{
    /// <summary>Gets or sets the id of the signed-in user.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the name of the signed-in user.</summary>
    public string? UserName { get; set; }

    /// <summary>Gets or sets the client address, for rate limiting anonymous callers and the audit log.</summary>
    public string? RemoteAddress { get; set; }

    /// <summary>Gets or sets the session of the caller, when known (audit log).</summary>
    public string? SessionId { get; set; }

    /// <summary>Gets or sets the permission check; <c>null</c> when permissions are not enforced.</summary>
    public Func<string, bool>? HasPermission { get; set; }
}

/// <summary>
/// Provides the query, parameters, and options needed to execute a data explorer query.
/// </summary>
public class DataExplorerExecutionContext
{
    /// <summary>Gets or sets the query string to execute.</summary>
    public required string Query { get; set; }

    /// <summary>Gets or sets the query parameters.</summary>
    public Dictionary<string, object> Parameters { get; set; } = [];

    /// <summary>Gets or sets the provider options associated with this execution.</summary>
    public required DataExplorerProviderOptions Options { get; set; }
}

/// <summary>
/// Contains the result of a data explorer query execution, including rows, columns, and metadata.
/// </summary>
public class DataExplorerResult
{
    /// <summary>Gets or sets whether the query executed successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the name of the data source that was queried.</summary>
    public required string Source { get; set; }

    /// <summary>Gets or sets the query language used.</summary>
    public DataExplorerLanguage Language { get; set; }

    /// <summary>Gets or sets the default output format for this result.</summary>
    public DataExplorerOutputFormat DefaultOutputFormat { get; set; }

    /// <summary>Gets or sets the query execution time in milliseconds.</summary>
    public long ExecutionTime { get; set; }

    /// <summary>Gets or sets the column names in the result set.</summary>
    public List<string>? Columns { get; set; }

    /// <summary>Gets or sets the result rows.</summary>
    public List<object>? Rows { get; set; }

    /// <summary>Gets or sets the total number of rows returned.</summary>
    public int RowCount { get; set; }

    /// <summary>Gets or sets whether the result was truncated due to the max rows limit.</summary>
    public bool Truncated { get; set; }

    /// <summary>Gets or sets the error message if the query failed.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// Why the server refused the query, when it did (see <see cref="DataExplorerErrorCodes"/>): <c>forbidden</c>
    /// (403), <c>rate_limited</c> (429), <c>read_only</c>, <c>object_not_allowed</c>, <c>unknown_source</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }
}

/// <summary>
/// Represents an incoming request to execute a data explorer query.
/// </summary>
public class DataExplorerExecuteRequest
{
    /// <summary>Gets or sets the target data source name.</summary>
    public required string Source { get; set; }

    /// <summary>Gets or sets the query string to execute.</summary>
    public required string Query { get; set; }

    /// <summary>Gets or sets optional query parameters.</summary>
    public Dictionary<string, object>? Parameters { get; set; }
}

/// <summary>
/// Describes an available data explorer source, including its language, templates, and parameters.
/// </summary>
public class DataExplorerSourceInfo
{
    /// <summary>Gets or sets the data source name.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the data source description.</summary>
    public required string Description { get; set; }

    /// <summary>Gets or sets the query language.</summary>
    public DataExplorerLanguage Language { get; set; }

    /// <summary>Gets or sets the default output format.</summary>
    public DataExplorerOutputFormat DefaultOutputFormat { get; set; }

    /// <summary>Gets or sets the available query templates.</summary>
    public List<DataExplorerTemplate> Templates { get; set; } = [];

    /// <summary>Gets or sets the accepted query parameters.</summary>
    public List<DataExplorerParameterDescriptor> Parameters { get; set; } = [];

    /// <summary>Gets or sets whether queries may only read.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Gets or sets whether the caller may export results (<c>json</c>/<c>csv</c>).</summary>
    public bool CanExport { get; set; }
}

/// <summary>
/// Describes a column in a data explorer schema.
/// </summary>
public class DataExplorerSchemaColumn
{
    /// <summary>Gets or sets the column name.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the column data type.</summary>
    public required string Type { get; set; }

    /// <summary>Gets or sets whether the column allows null values.</summary>
    public bool Nullable { get; set; }

    /// <summary>Gets or sets whether this column is a primary key.</summary>
    public bool PrimaryKey { get; set; }
}

/// <summary>
/// Describes a table or collection in a data explorer schema.
/// </summary>
public class DataExplorerSchemaTable
{
    /// <summary>Gets or sets the table name.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the table type (e.g., "table", "view", "collection").</summary>
    public required string Type { get; set; }

    /// <summary>Gets or sets the columns belonging to this table.</summary>
    public List<DataExplorerSchemaColumn> Columns { get; set; } = [];
}

/// <summary>
/// Contains the schema introspection result for a data explorer source.
/// </summary>
public class DataExplorerSchemaResult
{
    /// <summary>Gets or sets the data source name.</summary>
    public required string Source { get; set; }

    /// <summary>Gets or sets the tables discovered in the schema.</summary>
    public List<DataExplorerSchemaTable> Tables { get; set; } = [];
}
