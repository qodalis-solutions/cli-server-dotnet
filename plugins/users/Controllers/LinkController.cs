using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;

namespace Qodalis.Cli.Plugin.Users.Controllers;

/// <summary>
/// Link requests (<c>server add</c> in the web CLI, through the browser):
/// <list type="bullet">
/// <item><c>POST /link</c> — a terminal asks to be linked (public, 10 per minute per client address, 429)
/// → 201 <c>{ linkId, userCode, verificationUrl, expiresIn, interval }</c></item>
/// <item><c>GET  /link/{linkId}</c> — the terminal polls (public) → <c>{ status }</c>, the token once approved;
/// 404 <c>{ status: "expired" }</c></item>
/// <item><c>GET  /link/code/{userCode}</c> — the approval page reads the request (signed in)</item>
/// <item><c>POST /link/code/{userCode}/approve</c> — creates the approver's API token for the terminal (session only;
/// 501 with an external auth adapter)</item>
/// <item><c>POST /link/code/{userCode}/deny</c> → 204</item>
/// </list>
/// </summary>
[Route("api/v1/qcli/link")]
public class LinkController : UsersControllerBase
{
    private readonly LinkService _links;
    private readonly IpRateLimiter _ip;

    public LinkController(LinkService links, IpRateLimiter ip)
    {
        _links = links;
        _ip = ip;
    }

    /// <summary>Creates a link request.</summary>
    [HttpPost]
    [UsersAnonymous]
    public IActionResult Create([FromBody] JsonElement body)
    {
        var address = _ip.ClientIp(HttpContext);
        if (!_links.TryAcquire(address))
            throw new UsersApiException(429, "too many link requests, try again later");
        var origin = Request.Headers.Origin.FirstOrDefault();
        return Json(201, _links.Create(body, origin, address));
    }

    /// <summary>The request the approval page shows.</summary>
    [HttpGet("code/{code}")]
    public IActionResult View([FromRoute] string code) => JsonOk(_links.View(code));

    /// <summary>Approves the request: the caller's API token for the terminal.</summary>
    [HttpPost("code/{code}/approve")]
    [UsersCredentialEndpoint(ApiTokens = true)]
    public async Task<IActionResult> Approve([FromRoute] string code) => JsonOk(await _links.ApproveAsync(Auth, code));

    /// <summary>Denies the request.</summary>
    [HttpPost("code/{code}/deny")]
    public async Task<IActionResult> Deny([FromRoute] string code)
    {
        await _links.DenyAsync(Auth, code);
        return NoContent();
    }

    /// <summary>The terminal polls its request; the token is handed out once.</summary>
    [HttpGet("{linkId}")]
    [UsersAnonymous]
    public IActionResult Poll([FromRoute] string linkId)
    {
        var result = _links.Poll(linkId);
        if (result == null) return Json(404, new LinkPoll { Status = "expired" });
        Response.Headers.CacheControl = "no-store";
        return JsonOk(result);
    }
}
