using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Mcp;

/// <summary>A CLI command exposed as an MCP tool.</summary>
public sealed class CliMcpTool
{
    internal CliMcpTool(string name, IReadOnlyList<string> path, ICliCommandProcessor processor,
        IReadOnlyList<string> requiredPermissions)
    {
        Name = name;
        Path = path;
        Processor = processor;
        RequiredPermissions = requiredPermissions;
    }

    /// <summary>Tool name: the command path joined with <c>_</c>, lower-cased, <c>[a-z0-9_-]</c>, at most 64 chars.</summary>
    public string Name { get; }

    /// <summary>The command path (root command, then sub-commands).</summary>
    public IReadOnlyList<string> Path { get; }

    public ICliCommandProcessor Processor { get; }

    /// <summary>Every <see cref="ICliCommandProcessor.RequiredPermissions"/> along the chain.</summary>
    public IReadOnlyList<string> RequiredPermissions { get; }

    /// <summary>
    /// True when the command takes a positional value (<see cref="ICliCommandProcessor.ValueRequired"/> or
    /// <see cref="ICliCommandProcessor.AcceptsRawInput"/>), exposed as the <c>value</c> argument.
    /// </summary>
    public bool TakesValue => TakesValueOf(Processor);

    internal static bool TakesValueOf(ICliCommandProcessor processor) =>
        processor.ValueRequired == true || processor.AcceptsRawInput == true;

    public string Description => string.IsNullOrWhiteSpace(Processor.Description)
        ? $"Run '{string.Join(' ', Path)}'"
        : Processor.Description;

    public IReadOnlyList<ICliCommandParameterDescriptor> Parameters =>
        Processor.Parameters?.Where(p => !string.IsNullOrEmpty(p.Name)).ToList() ?? [];

    /// <summary>JSON Schema of the tool arguments.</summary>
    public JsonObject InputSchema()
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in Parameters)
        {
            if (properties.ContainsKey(p.Name)) continue;
            var prop = new JsonObject { ["type"] = JsonType(p.Type) };
            if (p.Type == CommandParameterType.Array) prop["items"] = new JsonObject { ["type"] = "string" };
            if (!string.IsNullOrEmpty(p.Description)) prop["description"] = p.Description;
            if (p.DefaultValue != null)
            {
                try
                {
                    prop["default"] = System.Text.Json.JsonSerializer.SerializeToNode(p.DefaultValue);
                }
                catch (Exception)
                {
                    // Not serializable: no default in the schema.
                }
            }
            properties[p.Name] = prop;
            if (p.Required) required.Add(p.Name);
        }

        if (TakesValue && !properties.ContainsKey("value"))
        {
            properties["value"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The command's positional input",
            };
            if (Processor.ValueRequired == true) required.Add("value");
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    /// <summary>JSON Schema type of a parameter: string, number, boolean, array (of strings); anything else string.</summary>
    internal static string JsonType(CommandParameterType type) => type switch
    {
        CommandParameterType.Number => "number",
        CommandParameterType.Boolean => "boolean",
        CommandParameterType.Array => "array",
        _ => "string",
    };
}

/// <summary>
/// The commands provider: one tool per CLI command (see <see cref="AllTools"/>). Calls build the command
/// the CLI would send and run it through the same executor as <c>/execute</c>. Requires
/// <c>commands:execute</c> plus every <see cref="ICliCommandProcessor.RequiredPermissions"/> along the chain.
/// </summary>
public sealed class CommandsMcpToolProvider : IMcpToolProvider
{
    public const int MaxToolNameLength = 64;

    private readonly ICliCommandRegistry _registry;
    private readonly ICliCommandExecutorService _executor;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, byte> _warnedCollisions = new();

    public CommandsMcpToolProvider(ICliCommandRegistry registry, ICliCommandExecutorService executor, ILogger logger)
    {
        _registry = registry;
        _executor = executor;
        _logger = logger;
    }

    public string? Feature => "commands";

    public Task<IReadOnlyList<McpToolDefinition>> GetToolsAsync(McpToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<McpToolDefinition>>(AllTools().Select(ToDefinition).ToList());

    private McpToolDefinition ToDefinition(CliMcpTool tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchema = tool.InputSchema(),
        RequiredPermissions = [CliRoutePolicy.CommandsExecute, .. tool.RequiredPermissions],
        CallAsync = async (args, context, ct) =>
        {
            var command = CliMcpServer.BuildCommand(tool, args);
            _logger.LogDebug("MCP tool call: {Tool} (user={User})", tool.Name, context.Principal?.UserName ?? "-");
            var response = await _executor.ExecuteAsync(command, ct);
            return CliMcpServer.CallResult(response);
        },
    };

    /// <summary>
    /// Every command tool. A processor becomes a tool when it has no sub-processors, declares parameters,
    /// or takes a value. On a name collision the first wins (a warning is logged once per name).
    /// </summary>
    public List<CliMcpTool> AllTools()
    {
        var tools = new List<CliMcpTool>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in _registry.Processors.ToList())
            Walk(root, [], [], tools, names);
        return tools;
    }

    /// <summary>
    /// The command path joined with <c>_</c>, lower-cased, characters outside <c>[a-z0-9_-]</c>
    /// replaced by <c>-</c>, at most 64 characters.
    /// </summary>
    public static string ToolName(IEnumerable<string> path)
    {
        var joined = string.Join('_', path).ToLowerInvariant();
        var sb = new StringBuilder(joined.Length);
        foreach (var c in joined)
            sb.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' ? c : '-');
        var name = sb.ToString();
        return name.Length > MaxToolNameLength ? name[..MaxToolNameLength] : name;
    }

    private void Walk(ICliCommandProcessor processor, List<string> parentPath, List<string> parentPermissions,
        List<CliMcpTool> tools, HashSet<string> names)
    {
        if (string.IsNullOrWhiteSpace(processor.Command)) return;

        var path = new List<string>(parentPath) { processor.Command };
        var permissions = new List<string>(parentPermissions);
        if (processor.RequiredPermissions != null) permissions.AddRange(processor.RequiredPermissions);

        var children = processor.Processors?.ToList() ?? [];
        var hasParameters = processor.Parameters?.Any() == true;
        if (children.Count == 0 || hasParameters || CliMcpTool.TakesValueOf(processor))
        {
            var name = ToolName(path);
            if (names.Add(name))
            {
                tools.Add(new CliMcpTool(name, path, processor,
                    permissions.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
            }
            else if (_warnedCollisions.TryAdd(name, 0))
            {
                _logger.LogWarning("MCP tool name collision: '{Tool}' for command '{Command}' is already used; the first one wins",
                    name, string.Join(' ', path));
            }
        }

        foreach (var child in children)
            Walk(child, path, permissions, tools, names);
    }
}
