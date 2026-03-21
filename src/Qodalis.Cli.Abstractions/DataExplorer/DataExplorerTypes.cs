using System.Text.Json.Serialization;
using Qodalis.Cli.Abstractions.Helpers;

namespace Qodalis.Cli.Abstractions.DataExplorer;

[JsonConverter(typeof(CamelCaseEnumConverter))]
public enum DataExplorerLanguage { Sql, Json, Shell, Graphql, Redis, Elasticsearch }

[JsonConverter(typeof(CamelCaseEnumConverter))]
public enum DataExplorerOutputFormat { Table, Json, Csv, Raw }

public class DataExplorerTemplate
{
    public required string Name { get; set; }
    public required string Query { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, object>? Parameters { get; set; }
}

public class DataExplorerParameterDescriptor
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool Required { get; set; }
    public object? DefaultValue { get; set; }
}

public class DataExplorerProviderOptions
{
    public required string Name { get; set; }
    public required string Description { get; set; }
    public DataExplorerLanguage Language { get; set; } = DataExplorerLanguage.Sql;
    public DataExplorerOutputFormat DefaultOutputFormat { get; set; } = DataExplorerOutputFormat.Table;
    public List<DataExplorerParameterDescriptor> Parameters { get; set; } = [];
    public List<DataExplorerTemplate> Templates { get; set; } = [];
    public int Timeout { get; set; } = 30000;
    public int MaxRows { get; set; } = 1000;
}

public class DataExplorerExecutionContext
{
    public required string Query { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = [];
    public required DataExplorerProviderOptions Options { get; set; }
}

public class DataExplorerResult
{
    public bool Success { get; set; }
    public required string Source { get; set; }
    public DataExplorerLanguage Language { get; set; }
    public DataExplorerOutputFormat DefaultOutputFormat { get; set; }
    public long ExecutionTime { get; set; }
    public List<string>? Columns { get; set; }
    public List<object>? Rows { get; set; }
    public int RowCount { get; set; }
    public bool Truncated { get; set; }
    public string? Error { get; set; }
}

public class DataExplorerExecuteRequest
{
    public required string Source { get; set; }
    public required string Query { get; set; }
    public Dictionary<string, object>? Parameters { get; set; }
}

public class DataExplorerSourceInfo
{
    public required string Name { get; set; }
    public required string Description { get; set; }
    public DataExplorerLanguage Language { get; set; }
    public DataExplorerOutputFormat DefaultOutputFormat { get; set; }
    public List<DataExplorerTemplate> Templates { get; set; } = [];
    public List<DataExplorerParameterDescriptor> Parameters { get; set; } = [];
}

public class DataExplorerSchemaColumn
{
    public required string Name { get; set; }
    public required string Type { get; set; }
    public bool Nullable { get; set; }
    public bool PrimaryKey { get; set; }
}

public class DataExplorerSchemaTable
{
    public required string Name { get; set; }
    public required string Type { get; set; }
    public List<DataExplorerSchemaColumn> Columns { get; set; } = [];
}

public class DataExplorerSchemaResult
{
    public required string Source { get; set; }
    public List<DataExplorerSchemaTable> Tables { get; set; } = [];
}
