using System.Text.Json;
using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.Notifications;
using Qodalis.Cli.Notifications;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Mcp;

/// <summary><c>notifications_list</c>: the caller's system notifications (read-only, no permission; marking them read stays a user's action).</summary>
public sealed class NotificationsMcpToolProvider(CliNotificationService notifications, CliServerOptions options) : IMcpToolProvider
{
    public string? Feature => "notifications";

    public Task<IReadOnlyList<McpToolDefinition>> GetToolsAsync(McpToolContext context, CancellationToken cancellationToken)
    {
        if (!options.CliRoutes) return Task.FromResult<IReadOnlyList<McpToolDefinition>>([]);
        IReadOnlyList<McpToolDefinition> tools =
        [
            new()
            {
                Name = "notifications_list",
                Description = "List the server's system notifications the caller may see (available updates, restart required, plugins that failed to load or need configuration, security warnings), newest first, with the unread count",
                InputSchema = McpSchema.Object(new JsonObject
                {
                    ["unread"] = McpSchema.Property("boolean", "Only the unread ones"),
                    ["severity"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray(CliNotificationSeverities.All.Select(s => (JsonNode?)s).ToArray()),
                        ["description"] = "Only this severity",
                    },
                    ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 500, ["description"] = "At most this many (default 50)" },
                }),
                RequiredPermissions = [],
                Annotations = McpToolAnnotations.ReadOnly,
                CallAsync = (args, ctx, _) =>
                {
                    var limit = McpSchema.GetInt64(args, "limit");
                    if (limit is < 1 or > 500) return Task.FromResult(McpToolResult.Error("limit must be an integer from 1 to 500"));
                    var result = notifications.List(CliNotificationViewer.Of(ctx.Principal, options), McpSchema.GetBoolean(args, "unread") == true,
                        McpSchema.GetString(args, "severity"), (int)(limit ?? 50));
                    var text = result.Items.Count > 0
                        ? string.Join('\n', result.Items.Select(n => $"{(n.Read == true ? " " : "*")} [{n.Severity}] {n.Title}{(n.Message.Length > 0 ? $": {n.Message}" : "")}"))
                        : "No notifications.";
                    var structured = JsonSerializer.SerializeToNode(result, McpJson.Options)!.AsObject();
                    return Task.FromResult(McpToolResult.Ok($"{result.UnreadCount} unread\n{text}", structured));
                },
            },
        ];
        return Task.FromResult(tools);
    }
}
