using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qodalis.Cli.Abstractions.Helpers;

public class CamelCaseEnumConverter()
    : JsonStringEnumConverter(JsonNamingPolicy.CamelCase);
