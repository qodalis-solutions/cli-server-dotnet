using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Mcp;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Controllers;

/// <summary>
/// MCP endpoint (Streamable HTTP, stateless, JSON responses only). Enabled with <c>CliBuilder.AddMcp()</c>;
/// answers 404 otherwise. <c>GET</c>/<c>DELETE</c> → 405 (<c>Allow: POST</c>). A present <c>Origin</c> must be
/// allowed or same-origin (403). With an authentication provider (users plugin) the request is authenticated
/// by it (<c>commands:execute</c>); without one, <see cref="McpOptions.Token"/> (when set) is required as a
/// bearer token (401).
/// </summary>
[Route("api/qcli/mcp")]
public class McpController : ControllerBase
{
    private readonly CliMcpServer _server;
    private readonly CliServerOptions _options;

    public McpController(CliMcpServer server, CliServerOptions options)
    {
        _server = server;
        _options = options;
    }

    private bool Enabled => _options.Mcp is { Enabled: true };

    /// <summary>One JSON-RPC message → JSON response (202 for notifications).</summary>
    [HttpPost]
    public async Task<IActionResult> Post(CancellationToken cancellationToken)
    {
        if (!Enabled) return NotFound(new { error = "not found" });
        var gate = Gate();
        if (gate != null) return gate;

        var principal = HttpContext.GetCliPrincipal();
        if (_options.RequireAuthentication)
        {
            // Authenticated (and commands:execute checked) by the server auth gate; fail closed.
            if (principal == null) return StatusCode(401, new { error = "unauthorized" });
        }
        else if (!string.IsNullOrEmpty(_options.Mcp!.Token) && !TokenMatches(BearerToken(), _options.Mcp.Token))
        {
            return StatusCode(401, new { error = "unauthorized" });
        }

        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            body = await reader.ReadToEndAsync(cancellationToken);

        var response = await _server.HandleAsync(body, new McpToolContext(principal, HttpContext.RequestServices, HttpContext),
            cancellationToken);
        if (response.Json == null) return StatusCode(response.StatusCode);
        return new ContentResult
        {
            StatusCode = response.StatusCode,
            ContentType = "application/json",
            Content = response.Json,
        };
    }

    /// <summary>No SSE stream and no sessions: 405 with <c>Allow: POST</c>.</summary>
    [HttpGet]
    [HttpDelete]
    public IActionResult NotAllowed()
    {
        if (!Enabled) return NotFound(new { error = "not found" });
        return Gate() ?? StatusCode(405, new { error = "method not allowed" });
    }

    /// <summary>Origin (403) and method (405) checks, shared with the pre-authentication gate.</summary>
    private IActionResult? Gate()
    {
        var rejection = CliMcpGate.Check(HttpContext);
        if (rejection == null) return null;
        if (rejection.Allow != null) Response.Headers.Allow = rejection.Allow;
        return StatusCode(rejection.StatusCode, new { error = rejection.Error });
    }

    private string? BearerToken()
    {
        var header = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = header["Bearer ".Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>Constant-time comparison (of SHA-256 digests, so lengths do not leak).</summary>
    private static bool TokenMatches(string? presented, string expected)
    {
        if (presented == null) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
}
