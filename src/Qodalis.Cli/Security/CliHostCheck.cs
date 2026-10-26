using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Qodalis.Cli.Security;

/// <summary>
/// DNS-rebinding protection for the CLI routes and WebSockets: a page on <c>attacker.example</c> that
/// re-resolves its own name to this server's address is same-origin with it in the browser, so Origin
/// checks pass; only the <c>Host</c> header still names the attacker's domain. Requests whose Host is not
/// allowed (see <see cref="CliServerOptions.IsHostAllowed"/>) answer <c>421 Misdirected Request</c>.
/// Applied by <c>UseCli()</c>, the global MVC filter and the MCP gate.
/// </summary>
public static class CliHostCheck
{
    /// <summary>The error returned with the 421 response.</summary>
    public const string Error = "misdirected request: host not allowed";

    /// <summary>
    /// True when this request targets a CLI route or WebSocket and must be rejected because of its Host header.
    /// </summary>
    public static bool IsRejected(HttpContext context)
    {
        if (!CliRoutePolicy.IsCliPath((context.Request.Path.Value ?? string.Empty).ToLowerInvariant())) return false;
        var options = context.RequestServices.GetService<CliServerOptions>();
        if (options == null || !options.EnforcesHostCheck) return false;
        return !options.IsHostAllowed(context.Request.Host.Value);
    }

    /// <summary>Writes the 421 JSON response when the request is rejected; returns true when it was answered.</summary>
    public static async Task<bool> TryRejectAsync(HttpContext context)
    {
        if (!IsRejected(context)) return false;
        context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = Error }));
        return true;
    }
}
