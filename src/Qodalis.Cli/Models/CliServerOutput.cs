using System.Text.Json.Serialization;

namespace Qodalis.Cli.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextOutput), "text")]
[JsonDerivedType(typeof(TableOutput), "table")]
[JsonDerivedType(typeof(ListOutput), "list")]
[JsonDerivedType(typeof(JsonOutput), "json")]
[JsonDerivedType(typeof(KeyValueOutput), "key-value")]
public abstract class CliServerOutput
{
}

public class TextOutput : CliServerOutput
{
    public required string Value { get; set; }
    public string? Style { get; set; }
}

public class TableOutput : CliServerOutput
{
    public required string[] Headers { get; set; }
    public required string[][] Rows { get; set; }
}

public class ListOutput : CliServerOutput
{
    public required string[] Items { get; set; }
    public bool? Ordered { get; set; }
}

public class JsonOutput : CliServerOutput
{
    public required object Value { get; set; }
}

public class KeyValueOutput : CliServerOutput
{
    public required KeyValueEntry[] Entries { get; set; }
}

public class KeyValueEntry
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}
