using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Mcp;

/// <summary>MCP tool annotations (hints for clients; never relied upon for security).</summary>
public sealed class McpToolAnnotations
{
    /// <summary>The tool does not modify its environment.</summary>
    public bool? ReadOnlyHint { get; init; }

    /// <summary>The tool may perform destructive updates.</summary>
    public bool? DestructiveHint { get; init; }

    /// <summary>Calling the tool repeatedly with the same arguments has no additional effect.</summary>
    public bool? IdempotentHint { get; init; }

    public static readonly McpToolAnnotations ReadOnly = new() { ReadOnlyHint = true };
    public static readonly McpToolAnnotations Destructive = new() { DestructiveHint = true };

    internal JsonObject ToJson()
    {
        var json = new JsonObject();
        if (ReadOnlyHint.HasValue) json["readOnlyHint"] = ReadOnlyHint.Value;
        if (DestructiveHint.HasValue) json["destructiveHint"] = DestructiveHint.Value;
        if (IdempotentHint.HasValue) json["idempotentHint"] = IdempotentHint.Value;
        return json;
    }
}

/// <summary>The caller of an MCP request, passed to tool providers and tool calls.</summary>
public sealed class McpToolContext
{
    public McpToolContext(CliPrincipal? principal, IServiceProvider services, HttpContext? httpContext = null)
    {
        Principal = principal;
        Services = services;
        HttpContext = httpContext;
    }

    /// <summary>The authenticated caller; null when the server has no authentication provider.</summary>
    public CliPrincipal? Principal { get; }

    /// <summary>Request services.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The HTTP request carrying the MCP message, when available.</summary>
    public HttpContext? HttpContext { get; }
}

/// <summary>Result of an MCP tool call: plain text, optional structured content, and the error flag.</summary>
public sealed class McpToolResult
{
    public required string Text { get; init; }

    /// <summary>Returned as <c>structuredContent</c> (must be a JSON object).</summary>
    public JsonObject? Structured { get; init; }

    public bool IsError { get; init; }

    public static McpToolResult Ok(string text, JsonObject? structured = null) =>
        new() { Text = text, Structured = structured };

    public static McpToolResult Error(string text) => new() { Text = text, IsError = true };

    internal JsonObject ToJson()
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Text }),
            ["isError"] = IsError,
        };
        if (Structured != null) result["structuredContent"] = Structured.DeepClone();
        return result;
    }
}

/// <summary>An MCP tool contributed by an <see cref="IMcpToolProvider"/>.</summary>
public sealed class McpToolDefinition
{
    /// <summary><c>[a-z0-9_-]</c>, at most 64 characters.</summary>
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>JSON Schema of the arguments: <c>type: object</c>, <c>additionalProperties: false</c>.</summary>
    public required JsonObject InputSchema { get; init; }

    /// <summary>Permissions checked for <c>tools/list</c> and <c>tools/call</c> (only when the server is protected).</summary>
    public IReadOnlyList<string> RequiredPermissions { get; init; } = [];

    public McpToolAnnotations? Annotations { get; init; }

    /// <summary>
    /// Runs the tool. Arguments were validated against <see cref="InputSchema"/> first. Exceptions become
    /// <c>isError: true</c> results with the message.
    /// </summary>
    public required Func<JsonObject, McpToolContext, CancellationToken, Task<McpToolResult>> CallAsync { get; init; }
}

/// <summary>
/// Contributes MCP tools. Providers are asked for their tools on every MCP request, so registrations,
/// enabled features and the caller's permissions are always current. Register with
/// <see cref="McpServiceCollectionExtensions.AddMcpToolProvider{T}"/>.
/// </summary>
public interface IMcpToolProvider
{
    /// <summary>Feature name reported by the <c>server_info</c> tool (e.g. <c>jobs</c>); null for none.</summary>
    string? Feature => null;

    /// <summary>The provider's tools (empty when its feature is disabled).</summary>
    Task<IReadOnlyList<McpToolDefinition>> GetToolsAsync(McpToolContext context, CancellationToken cancellationToken);
}

/// <summary>Registration of MCP tool providers.</summary>
public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Registers an MCP tool provider (singleton, constructed through DI). Feature/plugin providers win name
    /// collisions over command tools; between providers the first registered wins.
    /// </summary>
    public static IServiceCollection AddMcpToolProvider<T>(this IServiceCollection services) where T : class, IMcpToolProvider
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpToolProvider, T>());
        return services;
    }

    /// <summary>Registers an MCP tool provider instance.</summary>
    public static IServiceCollection AddMcpToolProvider(this IServiceCollection services, IMcpToolProvider provider)
    {
        services.AddSingleton(provider);
        return services;
    }
}

/// <summary>Helpers to build and validate tool input schemas.</summary>
public static class McpSchema
{
    /// <summary><c>{ type: object, properties, required, additionalProperties: false }</c>.</summary>
    public static JsonObject Object(JsonObject? properties = null, params string[] required) => new()
    {
        ["type"] = "object",
        ["properties"] = properties ?? new JsonObject(),
        ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
        ["additionalProperties"] = false,
    };

    public static JsonObject Property(string type, string? description = null, JsonNode? defaultValue = null)
    {
        var prop = new JsonObject { ["type"] = type };
        if (type == "array") prop["items"] = new JsonObject { ["type"] = "string" };
        if (description != null) prop["description"] = description;
        if (defaultValue != null) prop["default"] = defaultValue;
        return prop;
    }

    /// <summary>
    /// Validates <paramref name="args"/> against <paramref name="schema"/> (types <c>string</c>, <c>number</c>,
    /// <c>integer</c>, <c>boolean</c>, <c>array</c> (with <c>items.type</c>), <c>object</c>; unknown properties when
    /// <c>additionalProperties</c> is false; required). Returns the reason when invalid. Null values count as absent.
    /// </summary>
    public static string? Validate(JsonObject schema, JsonObject args)
    {
        var properties = schema["properties"] as JsonObject ?? [];
        var closed = schema["additionalProperties"] is JsonValue ap && ap.GetValueKind() == JsonValueKind.False;
        foreach (var (key, value) in args)
        {
            if (!properties.TryGetPropertyValue(key, out var prop) || prop is not JsonObject propSchema)
            {
                if (closed) return $"unknown argument '{key}'";
                continue;
            }
            if (value == null) continue;
            var reason = CheckType(key, propSchema, value);
            if (reason != null) return reason;
        }

        foreach (var required in schema["required"] as JsonArray ?? [])
        {
            var key = required?.GetValue<string>();
            if (key == null) continue;
            if (!args.TryGetPropertyValue(key, out var value) || value == null)
                return $"missing required argument '{key}'";
        }
        return null;
    }

    private static string? CheckType(string key, JsonObject propSchema, JsonNode value)
    {
        var type = (propSchema["type"] as JsonValue)?.GetValue<string>();
        var kind = value.GetValueKind();
        switch (type)
        {
            case "string":
                return kind == JsonValueKind.String ? null : $"argument '{key}' must be a string";
            case "number":
                return kind == JsonValueKind.Number ? null : $"argument '{key}' must be a number";
            case "integer":
                return kind == JsonValueKind.Number && IsInteger(value) ? null : $"argument '{key}' must be an integer";
            case "boolean":
                return kind is JsonValueKind.True or JsonValueKind.False ? null : $"argument '{key}' must be a boolean";
            case "object":
                return value is JsonObject ? null : $"argument '{key}' must be an object";
            case "array":
                if (value is not JsonArray array) return $"argument '{key}' must be an array";
                var itemType = ((propSchema["items"] as JsonObject)?["type"] as JsonValue)?.GetValue<string>();
                if (itemType == "string" && array.Any(i => i?.GetValueKind() != JsonValueKind.String))
                    return $"argument '{key}' must be an array of strings";
                return null;
            default:
                return null;
        }
    }

    private static bool IsInteger(JsonNode value)
    {
        var element = JsonSerializer.SerializeToElement(value);
        return element.TryGetInt64(out _) || (element.TryGetDouble(out var d) && Math.Abs(d % 1) == 0 && !double.IsInfinity(d));
    }

    /// <summary>Reads an optional argument as a string.</summary>
    public static string? GetString(JsonObject args, string key) =>
        args[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>Reads an optional argument as a long.</summary>
    public static long? GetInt64(JsonObject args, string key)
    {
        if (args[key] is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return null;
        var element = JsonSerializer.SerializeToElement(v);
        return element.TryGetInt64(out var l) ? l : (long)element.GetDouble();
    }

    /// <summary>Reads an optional argument as a boolean.</summary>
    public static bool? GetBoolean(JsonObject args, string key) =>
        args[key] is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;
}
