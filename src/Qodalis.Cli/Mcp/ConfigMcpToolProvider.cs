using System.Text.Json;
using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Mcp;

/// <summary>
/// <c>config_list</c>, <c>config_get</c> (read-only, <c>config:read</c>) and <c>config_set</c> (<c>config:write</c>, not
/// read-only: hidden by the endpoint's <see cref="McpOptions.ReadOnly"/> and the MCP client's <c>QCLI_READ_ONLY</c>).
/// Secrets are never returned.
/// </summary>
public sealed class ConfigMcpToolProvider : IMcpToolProvider
{
    private readonly ICliConfigService _service;
    private readonly CliServerOptions _serverOptions;

    public ConfigMcpToolProvider(ICliConfigService service, CliServerOptions serverOptions)
    {
        _service = service;
        _serverOptions = serverOptions;
    }

    public string? Feature => "config";

    private CliConfigContext Context(McpToolContext context)
    {
        var principal = context.Principal;
        if (principal == null) return CliConfigContexts.Anonymous(_serverOptions, "mcp");
        return new CliConfigContext(new CliConfigCaller(principal.UserId, principal.UserName, principal.Permissions),
            principal.Can, "mcp");
    }

    private static McpToolResult Json(object value)
    {
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), McpJson.Options) as JsonObject ?? new JsonObject();
        return McpToolResult.Ok(McpJson.Pretty(node), node);
    }

    private static async Task<McpToolResult> RunAsync(Func<Task<McpToolResult>> call)
    {
        try
        {
            return await call();
        }
        catch (CliConfigException ex)
        {
            var body = ex.ToJson();
            return new McpToolResult { Text = McpJson.Pretty(body), Structured = body, IsError = true };
        }
    }

    public Task<IReadOnlyList<McpToolDefinition>> GetToolsAsync(McpToolContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<McpToolDefinition> tools =
        [
            new()
            {
                Name = "config_list",
                Description = "The server's config providers (where each reads its settings from, and the store a stored one saves changes to), the default store, and their sections (ids, titles, whether writable, versions); read a section with config_get",
                InputSchema = McpSchema.Object(),
                RequiredPermissions = [CliConfigPermissions.Read],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = (_, ctx, ct) => RunAsync(async () =>
                {
                    var providers = await _service.ListAsync(Context(ctx), ct);
                    return Json(new
                    {
                        defaultStore = _service.DescribeDefaultStore(),
                        providers = providers.Select(p =>
                        {
                            var summary = McpJson.ToObject(new
                            {
                                id = p.Id,
                                title = p.Title,
                                description = p.Description,
                                writable = p.Writable,
                                storage = p.Storage,
                                store = p.Store,
                                sections = p.Sections.Select(s => new { id = s.Id, title = s.Title, mutable = s.Mutable, version = s.Version, keys = s.Entries.Count }),
                            })!;
                            if (p.Store == null) summary.Remove("store");
                            return summary;
                        }),
                    });
                }),
            },
            new()
            {
                Name = "config_get",
                Description = "A config provider, one of its sections, or one setting (provider, section, key): values, defaults, sources and validation rules; secrets show only isSet",
                InputSchema = McpSchema.Object(new JsonObject
                {
                    ["provider"] = McpSchema.Property("string", "Provider id (config_list)"),
                    ["section"] = McpSchema.Property("string", "Section id (optional)"),
                    ["key"] = McpSchema.Property("string", "Setting key (optional, with section)"),
                }, "provider"),
                RequiredPermissions = [CliConfigPermissions.Read],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = (args, ctx, ct) => RunAsync(async () =>
                {
                    var provider = await _service.GetAsync(McpSchema.GetString(args, "provider") ?? string.Empty, Context(ctx), ct);
                    var sectionId = McpSchema.GetString(args, "section");
                    if (sectionId == null) return Json(new { provider });
                    var section = provider.Sections.FirstOrDefault(s => s.Id == sectionId);
                    if (section == null) return McpToolResult.Error($"unknown config section: {provider.Id}.{sectionId}");
                    var key = McpSchema.GetString(args, "key");
                    if (key == null) return Json(new { provider = provider.Id, section });
                    var entry = section.Entries.FirstOrDefault(e => e.Key == key);
                    if (entry == null) return McpToolResult.Error($"unknown setting: {provider.Id}.{section.Id}.{key}");
                    return Json(new { provider = provider.Id, section = section.Id, version = section.Version, entry });
                }),
            },
            new()
            {
                Name = "config_set",
                Description = "Changes settings of a config section: changes maps keys to new values (null resets to the default, clears a secret); pass the version from config_get to refuse a stale change. Answers per-key errors when invalid",
                InputSchema = McpSchema.Object(new JsonObject
                {
                    ["provider"] = McpSchema.Property("string", "Provider id"),
                    ["section"] = McpSchema.Property("string", "Section id"),
                    ["changes"] = McpSchema.Property("object", "Key to new value (null resets)"),
                    ["version"] = McpSchema.Property("string", "The section version the change is based on (optional)"),
                }, "provider", "section", "changes"),
                RequiredPermissions = [CliConfigPermissions.Write],
                Annotations = new McpToolAnnotations { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = true },
                CallAsync = (args, ctx, ct) => RunAsync(async () =>
                {
                    var changes = (args["changes"] as JsonObject ?? []).ToDictionary(p => p.Key, p => p.Value?.DeepClone());
                    var result = await _service.UpdateAsync(
                        McpSchema.GetString(args, "provider") ?? string.Empty,
                        McpSchema.GetString(args, "section") ?? string.Empty,
                        new CliConfigUpdateRequest { Changes = changes, Version = McpSchema.GetString(args, "version") },
                        Context(ctx), ct);
                    return Json(result);
                }),
            },
        ];
        return Task.FromResult(tools);
    }
}
