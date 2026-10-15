using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.FileSystem;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Mcp;

/// <summary>Result of an MCP HTTP exchange: the status code and the JSON body (null for 202).</summary>
public sealed record CliMcpResponse(int StatusCode, string? Json);

/// <summary>
/// Stateless MCP server (JSON-RPC 2.0 over Streamable HTTP, JSON responses only): <c>initialize</c>,
/// <c>ping</c>, <c>tools/list</c> and <c>tools/call</c>. Tools are the registered CLI commands, rebuilt on every
/// request and filtered by the caller's permissions; calls run through the same executor as <c>/execute</c>.
/// </summary>
public sealed partial class CliMcpServer
{
    /// <summary>Supported protocol versions, newest first.</summary>
    public static readonly string[] SupportedProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26"];

    public const string ServerName = "qodalis-cli-server";

    /// <summary>The server package version (e.g. <c>1.0.0-beta.7</c>), reported by <c>initialize</c> and <c>server_info</c>.</summary>
    public static readonly string PackageVersion = ResolvePackageVersion();

    private static string ResolvePackageVersion()
    {
        var assembly = typeof(CliMcpServer).Assembly;
        var informational = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    private static readonly JsonSerializerOptions OutputJson = McpJson.Options;

    private readonly ICliCommandRegistry _registry;
    private readonly ICliCommandExecutorService _executor;
    private readonly ICliServerInfoService _serverInfo;
    private readonly CliServerOptions _serverOptions;
    private readonly ILogger<CliMcpServer> _logger;
    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<IMcpToolProvider> _providers;
    private readonly ConcurrentDictionary<string, byte> _warnedCollisions = new();

    public CliMcpServer(
        ICliCommandRegistry registry,
        ICliCommandExecutorService executor,
        ICliServerInfoService serverInfo,
        CliServerOptions serverOptions,
        ILogger<CliMcpServer> logger,
        IServiceProvider services,
        IEnumerable<IMcpToolProvider> providers)
    {
        _registry = registry;
        _executor = executor;
        _serverInfo = serverInfo;
        _serverOptions = serverOptions;
        _logger = logger;
        _services = services;

        // Order = collision priority: built-in feature tools, then plugin providers (registration order),
        // then command tools.
        var list = new List<IMcpToolProvider>
        {
            new ServerInfoProvider(this),
            new FileSystemMcpToolProvider(services.GetRequiredService<IFileStorageProvider>(),
                enabled: services.GetService<FileSystemOptions>() != null),
            new DataExplorerMcpToolProvider(services.GetRequiredService<IDataExplorerRegistry>(),
                services.GetRequiredService<IDataExplorerExecutorService>()),
        };
        list.AddRange(providers);
        list.Add(new CommandsMcpToolProvider(registry, executor, logger));
        _providers = list;
    }

    private McpOptions Options => _serverOptions.Mcp ?? new McpOptions();

    private sealed class JsonRpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    /// <summary>
    /// Every tool of every provider (before visibility filtering), with the providers' feature names.
    /// On a name collision the first tool wins (a warning is logged once per name).
    /// </summary>
    public async Task<(List<McpToolDefinition> Tools, List<string> Features)> CollectToolsAsync(McpToolContext context,
        CancellationToken cancellationToken)
    {
        var tools = new List<McpToolDefinition>();
        var features = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in _providers)
        {
            IReadOnlyList<McpToolDefinition> provided;
            try
            {
                provided = await provider.GetToolsAsync(context, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "MCP tool provider {Provider} failed", provider.GetType().Name);
                continue;
            }

            if (provided.Count > 0 && provider.Feature != null && !features.Contains(provider.Feature))
                features.Add(provider.Feature);
            foreach (var tool in provided)
            {
                if (names.Add(tool.Name)) tools.Add(tool);
                else if (_warnedCollisions.TryAdd(tool.Name, 0))
                    _logger.LogWarning("MCP tool name collision: '{Tool}' from {Provider} is skipped (the first one wins)",
                        tool.Name, provider.GetType().Name);
            }
        }
        return (tools, features);
    }

    /// <summary>
    /// Tools visible to the caller: allowed by <see cref="McpOptions.Tools"/> / <see cref="McpOptions.Exclude"/>
    /// and — when the server is protected — covered by the caller's permissions.
    /// </summary>
    public async Task<List<McpToolDefinition>> VisibleToolsAsync(McpToolContext context, CancellationToken cancellationToken) =>
        (await CollectToolsAsync(context, cancellationToken)).Tools.Where(t => IsVisible(t, context.Principal)).ToList();

    private bool IsVisible(McpToolDefinition tool, CliPrincipal? principal)
    {
        var options = Options;
        if (options.Tools is { Count: > 0 } allow && !allow.Any(p => MatchesPattern(tool.Name, p))) return false;
        if (options.Exclude is { Count: > 0 } deny && deny.Any(p => MatchesPattern(tool.Name, p))) return false;
        if (!_serverOptions.RequireAuthentication) return true;
        if (principal == null) return false;
        return CliCommandPermissions.Missing(principal, tool.RequiredPermissions).Count == 0;
    }

    /// <summary>Exact name, or a prefix when the pattern ends with <c>*</c> (case-insensitive).</summary>
    public static bool MatchesPattern(string name, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        return pattern.EndsWith('*')
            ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Handles one JSON-RPC message (the request body).</summary>
    public Task<CliMcpResponse> HandleAsync(string body, CliPrincipal? principal, CancellationToken cancellationToken) =>
        HandleAsync(body, new McpToolContext(principal, _services), cancellationToken);

    /// <summary>Handles one JSON-RPC message (the request body) for <paramref name="context"/>.</summary>
    public async Task<CliMcpResponse> HandleAsync(string body, McpToolContext context, CancellationToken cancellationToken)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return Error(null, ParseError, "Parse error");
        }

        if (message is not JsonObject request)
            return Error(null, InvalidRequest, "Invalid Request");

        var hasId = request.TryGetPropertyValue("id", out var rawId);
        var validId = rawId is JsonValue v &&
                      (v.GetValueKind() == JsonValueKind.String || v.GetValueKind() == JsonValueKind.Number);
        var id = validId ? rawId!.DeepClone() : null;

        if (request["jsonrpc"] is not JsonValue version || version.GetValueKind() != JsonValueKind.String ||
            version.GetValue<string>() != "2.0")
            return Error(id, InvalidRequest, "Invalid Request");

        // A JSON-RPC response from the client: nothing to answer.
        if (!request.ContainsKey("method") && hasId && (request.ContainsKey("result") || request.ContainsKey("error")))
            return new CliMcpResponse(202, null);

        if (request["method"] is not JsonValue methodValue || methodValue.GetValueKind() != JsonValueKind.String ||
            (hasId && !validId))
            return Error(id, InvalidRequest, "Invalid Request");

        // Notification (e.g. notifications/initialized): accepted, no body.
        if (!hasId) return new CliMcpResponse(202, null);

        var parameters = request["params"];
        if (parameters != null && parameters is not JsonObject)
            return Error(id, InvalidParams, "Invalid params: params must be an object");

        try
        {
            var result = await DispatchAsync(methodValue.GetValue<string>(), parameters as JsonObject ?? [], context,
                cancellationToken);
            return Result(id, result);
        }
        catch (JsonRpcException ex)
        {
            return Error(id, ex.Code, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP request failed");
            return Error(id, InternalError, "Internal error");
        }
    }

    private async Task<JsonNode> DispatchAsync(string method, JsonObject parameters, McpToolContext context,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                return Initialize(parameters);
            case "ping":
                return new JsonObject();
            case "tools/list":
                return new JsonObject
                {
                    ["tools"] = new JsonArray((await VisibleToolsAsync(context, cancellationToken)).Select(t =>
                    {
                        var json = new JsonObject
                        {
                            ["name"] = t.Name,
                            ["description"] = t.Description,
                            ["inputSchema"] = t.InputSchema.DeepClone(),
                        };
                        if (t.Annotations != null) json["annotations"] = t.Annotations.ToJson();
                        return (JsonNode)json;
                    }).ToArray()),
                };
            case "tools/call":
                return await CallToolAsync(parameters, context, cancellationToken);
            default:
                throw new JsonRpcException(MethodNotFound, $"Method not found: {method}");
        }
    }

    private JsonObject Initialize(JsonObject parameters)
    {
        if (parameters["protocolVersion"] is not JsonValue requested || requested.GetValueKind() != JsonValueKind.String)
            throw new JsonRpcException(InvalidParams, "Invalid params: protocolVersion is required");

        var version = requested.GetValue<string>();
        var result = new JsonObject
        {
            ["protocolVersion"] = SupportedProtocolVersions.Contains(version, StringComparer.Ordinal)
                ? version
                : SupportedProtocolVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = PackageVersion },
        };
        if (!string.IsNullOrEmpty(Options.Instructions)) result["instructions"] = Options.Instructions;
        return result;
    }

    private async Task<JsonNode> CallToolAsync(JsonObject parameters, McpToolContext context, CancellationToken cancellationToken)
    {
        if (parameters["name"] is not JsonValue nameValue || nameValue.GetValueKind() != JsonValueKind.String ||
            string.IsNullOrEmpty(nameValue.GetValue<string>()))
            throw new JsonRpcException(InvalidParams, "Invalid params: name is required");
        var name = nameValue.GetValue<string>();

        var argsNode = parameters["arguments"];
        if (argsNode != null && argsNode is not JsonObject)
            throw new JsonRpcException(InvalidParams, "Invalid params: arguments must be an object");
        var args = argsNode as JsonObject ?? [];

        var tool = (await VisibleToolsAsync(context, cancellationToken)).FirstOrDefault(t => t.Name == name)
            ?? throw new JsonRpcException(InvalidParams, $"Unknown tool: {name}");

        var invalid = McpSchema.Validate(tool.InputSchema, args);
        if (invalid != null)
            throw new JsonRpcException(InvalidParams, $"Invalid arguments for tool {name}: {invalid}");

        try
        {
            return (await tool.CallAsync(args, context, cancellationToken)).ToJson();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MCP tool {Tool} failed", name);
            return McpToolResult.Error($"Error executing command: {ex.Message}").ToJson();
        }
    }

    /// <summary>The <c>server_info</c> tool: version, capabilities, enabled features, registered command names.</summary>
    private sealed class ServerInfoProvider(CliMcpServer server) : IMcpToolProvider
    {
        public Task<IReadOnlyList<McpToolDefinition>> GetToolsAsync(McpToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<McpToolDefinition>>(
            [
                new McpToolDefinition
                {
                    Name = "server_info",
                    Description = "Show the server version, capabilities, enabled features and registered commands",
                    InputSchema = McpSchema.Object(),
                    Annotations = McpToolAnnotations.ReadOnly,
                    CallAsync = server.ServerInfoAsync,
                },
            ]);
    }

    private async Task<McpToolResult> ServerInfoAsync(JsonObject args, McpToolContext context, CancellationToken ct)
    {
        var (_, features) = await CollectToolsAsync(context, ct);
        if (_serverOptions.EnableShell) features.Add("shell");
        if (_serverOptions.RequireAuthentication) features.Add("authentication");
        features.Insert(0, "mcp");

        var structured = new JsonObject
        {
            ["name"] = ServerName,
            ["version"] = PackageVersion,
            ["capabilities"] = JsonSerializer.SerializeToNode(_serverInfo.GetCapabilities(), _serverInfo.GetCapabilities().GetType(), McpJson.Options),
            ["features"] = new JsonArray(features.Select(f => (JsonNode)f).ToArray()),
            ["commands"] = new JsonArray(_registry.Processors.Select(p => (JsonNode)p.Command).ToArray()),
        };
        return McpToolResult.Ok(McpJson.Pretty(structured), structured);
    }

    [GeneratedRegex(@"^[^\s""'=]+$")]
    private static partial Regex PlainTokenRegex();

    private static string Quote(string value) =>
        PlainTokenRegex().IsMatch(value) ? value : JsonSerializer.Serialize(value);

    /// <summary>The command the CLI would send for a tool call (<c>value</c> → positional input when the tool takes one).</summary>
    public static CliProcessCommand BuildCommand(CliMcpTool tool, JsonObject args)
    {
        var parameters = new Dictionary<string, JsonElement>();
        foreach (var (key, node) in args)
        {
            if (node != null) parameters[key] = JsonSerializer.SerializeToElement(node);
        }

        string? value = null;
        var takesValue = tool.TakesValue && !tool.Parameters.Any(p => p.Name == "value");
        if (takesValue && parameters.TryGetValue("value", out var v) && v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString();
            parameters.Remove("value");
        }

        var parts = new List<string>(tool.Path);
        if (value != null) parts.Add(Quote(value));
        foreach (var (key, element) in parameters)
        {
            parts.Add(element.ValueKind switch
            {
                JsonValueKind.True => $"--{key}",
                JsonValueKind.False => $"--{key}=false",
                JsonValueKind.Array => $"--{key}={Quote(string.Join(',', element.EnumerateArray().Select(e => e.ToString())))}",
                _ => $"--{key}={Quote(element.ToString())}",
            });
        }

        var command = new CliProcessCommand
        {
            Command = tool.Path[0],
            ChainCommands = tool.Path.Skip(1).ToList(),
            RawCommand = string.Join(' ', parts),
            Args = parameters.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value),
        };
        if (value != null) command.Value = value;
        return command;
    }

    /// <summary>
    /// <c>tools/call</c> result: the outputs as plain text, <c>isError</c> = <c>exitCode != 0</c>, and
    /// <c>structuredContent</c> when the outputs are exactly one json output whose value is an object.
    /// </summary>
    public static McpToolResult CallResult(CliServerResponse response)
    {
        var outputs = response.Outputs ?? [];
        JsonObject? structured = null;
        if (outputs.Count == 1 && outputs[0] is JsonOutput json && ToNode(json.Value) is JsonObject obj)
            structured = obj;
        return new McpToolResult { Text = RenderText(outputs), Structured = structured, IsError = response.ExitCode != 0 };
    }

    /// <summary>
    /// Renders outputs as plain text: text as-is; tables as header + rows joined with <c> | </c>; lists one
    /// item per line; json pretty-printed; key-value as <c>key: value</c>. Blocks are separated by new lines.
    /// </summary>
    public static string RenderText(IEnumerable<CliServerOutput> outputs)
    {
        var blocks = new List<string>();
        foreach (var output in outputs)
        {
            switch (output)
            {
                case TextOutput text:
                    blocks.Add(text.Value ?? string.Empty);
                    break;
                case TableOutput table:
                    var lines = new List<string> { string.Join(" | ", table.Headers ?? []) };
                    lines.AddRange((table.Rows ?? []).Select(r => string.Join(" | ", r ?? [])));
                    blocks.Add(string.Join('\n', lines));
                    break;
                case ListOutput list:
                    blocks.Add(string.Join('\n', list.Items ?? []));
                    break;
                case JsonOutput json:
                    blocks.Add(ToNode(json.Value)?.ToJsonString(OutputJson) ?? "null");
                    break;
                case KeyValueOutput kv:
                    blocks.Add(string.Join('\n', (kv.Entries ?? []).Select(e => $"{e.Key}: {e.Value}")));
                    break;
                default:
                    blocks.Add(JsonSerializer.Serialize(output, OutputJson));
                    break;
            }
        }
        return string.Join('\n', blocks);
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement element => element.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText()),
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), OutputJson),
    };

    private static CliMcpResponse Result(JsonNode? id, JsonNode result) =>
        new(200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString(McpJson.Compact));

    private static CliMcpResponse Error(JsonNode? id, int code, string message) =>
        new(200, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString(McpJson.Compact));
}
