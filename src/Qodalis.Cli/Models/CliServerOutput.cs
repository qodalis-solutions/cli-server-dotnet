using System.Text.Json.Serialization;

namespace Qodalis.Cli.Models;

/// <summary>
/// Base class for CLI server output blocks. Uses JSON polymorphism to serialize as text, table, list, JSON, or key-value.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextOutput), "text")]
[JsonDerivedType(typeof(TableOutput), "table")]
[JsonDerivedType(typeof(ListOutput), "list")]
[JsonDerivedType(typeof(JsonOutput), "json")]
[JsonDerivedType(typeof(KeyValueOutput), "key-value")]
public abstract class CliServerOutput
{
}

/// <summary>
/// A plain-text output block with optional styling.
/// </summary>
public class TextOutput : CliServerOutput
{
    /// <summary>Gets or sets the text content.</summary>
    public required string Value { get; set; }

    /// <summary>Gets or sets an optional style hint (e.g., "error", "success").</summary>
    public string? Style { get; set; }
}

/// <summary>
/// A tabular output block with column headers and rows.
/// </summary>
public class TableOutput : CliServerOutput
{
    /// <summary>Gets or sets the column headers.</summary>
    public required string[] Headers { get; set; }

    /// <summary>Gets or sets the table rows, each row being an array of cell values.</summary>
    public required string[][] Rows { get; set; }
}

/// <summary>
/// A list output block, optionally ordered.
/// </summary>
public class ListOutput : CliServerOutput
{
    /// <summary>Gets or sets the list items.</summary>
    public required string[] Items { get; set; }

    /// <summary>Gets or sets whether the list is ordered (numbered).</summary>
    public bool? Ordered { get; set; }
}

/// <summary>
/// A raw JSON output block.
/// </summary>
public class JsonOutput : CliServerOutput
{
    /// <summary>Gets or sets the JSON value to serialize.</summary>
    public required object Value { get; set; }
}

/// <summary>
/// A key-value pairs output block.
/// </summary>
public class KeyValueOutput : CliServerOutput
{
    /// <summary>Gets or sets the key-value entries.</summary>
    public required KeyValueEntry[] Entries { get; set; }
}

/// <summary>
/// A single key-value pair in a <see cref="KeyValueOutput"/>.
/// </summary>
public class KeyValueEntry
{
    /// <summary>Gets or sets the entry key.</summary>
    public required string Key { get; set; }

    /// <summary>Gets or sets the entry value.</summary>
    public required string Value { get; set; }
}
