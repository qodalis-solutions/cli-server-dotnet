using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Abstractions.Restart;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Controllers;

/// <summary>
/// <c>/api/v1/qcli/server/restart</c>: <c>GET</c> the status (<c>admin:access</c> on a protected server), <c>POST</c>
/// <c>{ reason? }</c> restarts (<c>server:restart</c>): 202 <c>{ restarting: true, pending }</c>, then the graceful
/// shutdown; 409 when not supported or already restarting, 403 on a server without sign-in unless
/// <c>AllowAnonymous</c>, 400 for a bad reason. The route permissions are checked by the server's auth gate.
/// </summary>
[ApiController]
[Route("api/v1/qcli/server/restart")]
public class CliServerRestartController(ICliServerRestart restart) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(restart.Status());
    }

    [HttpPost]
    public async Task<IActionResult> Post()
    {
        string? reason = null;
        using (var reader = new StreamReader(Request.Body))
        {
            var text = await reader.ReadToEndAsync(HttpContext.RequestAborted);
            if (!string.IsNullOrWhiteSpace(text))
            {
                JsonNode? body;
                try
                {
                    body = JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    return StatusCode(400, new { error = "invalid body" });
                }
                if (body is JsonObject o && o["reason"] is { } node)
                {
                    if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String) return StatusCode(400, new { error = "reason must be a string" });
                    reason = v.GetValue<string>();
                }
            }
        }
        var principal = HttpContext.GetCliPrincipal();
        try
        {
            var result = await restart.RestartAsync(new CliServerRestartRequest
            {
                Reason = reason,
                Actor = principal != null ? new CliServerRestartActor(principal.UserId, principal.UserName) : null,
                Via = "api",
            }, HttpContext.RequestAborted);
            return StatusCode(202, result);
        }
        catch (CliServerRestartException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Message });
        }
    }
}
