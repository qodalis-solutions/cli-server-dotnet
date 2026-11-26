using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Mcp;
using Qodalis.Cli.Plugin.Admin.Controllers;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary><c>updates_list</c>: the update checker's last result (<c>check: true</c> checks first); <c>updates:read</c> or <c>plugins:install</c> on a protected server.</summary>
internal sealed class UpdatesMcpToolProvider(IServiceProvider services) : IMcpToolProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string? Feature => "updates";

    public Task<IReadOnlyList<McpToolDefinition>> GetToolsAsync(McpToolContext context, CancellationToken cancellationToken)
    {
        if (services.GetService<UpdateChecker>() is not { } checker) return Task.FromResult<IReadOnlyList<McpToolDefinition>>([]);
        IReadOnlyList<McpToolDefinition> tools =
        [
            new()
            {
                Name = "updates_list",
                Description = "List the available updates: dashboard and server plugins installed from the marketplace with a newer version, plugins added in code with a newer package, and the server package; check: true asks the marketplace first",
                InputSchema = McpSchema.Object(new JsonObject { ["check"] = McpSchema.Property("boolean", "Check now instead of answering the last result") }),
                RequiredPermissions = [],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = async (args, ctx, _) =>
                {
                    var options = ctx.Services.GetService<Qodalis.Cli.Security.CliServerOptions>();
                    if (options?.RequireAuthentication == true && !UpdatesController.CanRead(ctx.Principal))
                        return McpToolResult.Error("forbidden: missing permission updates:read");
                    var result = McpSchema.GetBoolean(args, "check") == true ? await checker.CheckAsync() : checker.Result();
                    var items = result.DashboardPlugins.Concat(result.ServerPlugins).Concat(result.CodePlugins)
                        .Concat(result.Server is { UpdateAvailable: true } server ? [server] : []);
                    var lines = items.Select(i =>
                        $"{i.Kind} {i.Package}: {i.Current ?? "?"} -> {i.Latest ?? "?"} ({(i.CanUpdate ? "update from the dashboard" : i.Reason ?? "update in code")})").ToList();
                    var text = $"{result.Count} update(s) available; last checked {result.CheckedAt ?? "never"}{(result.Error != null ? $" ({result.Error})" : "")}"
                        + (lines.Count > 0 ? "\n" + string.Join('\n', lines) : "");
                    return McpToolResult.Ok(text, JsonSerializer.SerializeToNode(result, Json)!.AsObject());
                },
            },
        ];
        return Task.FromResult(tools);
    }
}
