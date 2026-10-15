using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Mcp;

/// <summary>A rejection of an MCP request by <see cref="CliMcpGate"/>.</summary>
public sealed record CliMcpRejection(int StatusCode, string Error, string? Allow = null);

/// <summary>
/// Checks of the MCP endpoint that run <b>before</b> authentication (authentication plugins call it first):
/// a present <c>Origin</c> must be allowed or same-origin (403, DNS-rebinding protection) and only <c>POST</c>
/// is supported (405 with <c>Allow: POST</c>). Does nothing when MCP is disabled or for other paths.
/// </summary>
public static class CliMcpGate
{
    public static bool IsMcpPath(string? path) =>
        string.Equals((path ?? string.Empty).TrimEnd('/'), CliRoutePolicy.McpPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>The rejection for this request, or null when it may proceed (to authentication).</summary>
    public static CliMcpRejection? Check(HttpContext context)
    {
        if (!IsMcpPath(context.Request.Path.Value)) return null;
        var options = context.RequestServices.GetService<CliServerOptions>();
        if (options?.Mcp is not { Enabled: true }) return null;

        var origin = context.Request.Headers.Origin.FirstOrDefault();
        if (!string.IsNullOrEmpty(origin) && !options.IsOriginAllowed(origin, context.Request))
            return new CliMcpRejection(StatusCodes.Status403Forbidden, "forbidden: origin not allowed");

        if (!HttpMethods.IsPost(context.Request.Method))
            return new CliMcpRejection(StatusCodes.Status405MethodNotAllowed, "method not allowed", "POST");
        return null;
    }

    /// <summary>Writes the rejection (if any) and returns true when the request was answered.</summary>
    public static async Task<bool> TryRejectAsync(HttpContext context)
    {
        var rejection = Check(context);
        if (rejection == null) return false;
        context.Response.StatusCode = rejection.StatusCode;
        if (rejection.Allow != null) context.Response.Headers.Allow = rejection.Allow;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = rejection.Error }));
        return true;
    }
}
