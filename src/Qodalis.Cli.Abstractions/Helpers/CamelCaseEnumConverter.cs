using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qodalis.Cli.Abstractions.Helpers;

/// <summary>
/// JSON converter that serializes enum values using camelCase naming (e.g., <c>String</c> becomes <c>"string"</c>).
/// </summary>
public class CamelCaseEnumConverter()
    : JsonStringEnumConverter(JsonNamingPolicy.CamelCase);
