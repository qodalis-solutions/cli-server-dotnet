using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.Controllers;

/// <summary>
/// API tokens resource (long-lived, revocable bearer tokens for MCP clients, CI and scripts):
/// <list type="bullet">
/// <item><c>GET    /tokens</c> — own tokens (<c>?all=true</c> with <c>tokens:read</c>: every user's)</item>
/// <item><c>POST   /tokens</c> — create (session only) → 201 <c>{ token, apiToken }</c>; the token is shown once</item>
/// <item><c>DELETE /tokens/{id}</c> — revoke (owner or <c>tokens:revoke</c>; otherwise 404) → 204</item>
/// </list>
/// Token creation answers 501 when an external auth adapter is configured (no sessions, no bearer tokens).
/// </summary>
[Route("api/v1/qcli/tokens")]
public class TokensController : UsersControllerBase
{
    private readonly AuthService _auth;
    private readonly IUserStore _userStore;

    public TokensController(AuthService auth, IUserStore userStore)
    {
        _auth = auth;
        _userStore = userStore;
    }

    /// <summary>List the caller's tokens, or every user's tokens with <c>?all=true</c> (<c>tokens:read</c>).</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? all)
    {
        var caller = Auth;
        if (all != null && all != "true" && all != "false")
            throw UsersApiException.BadRequest("all must be true or false");
        var listAll = all == "true";
        if (listAll && !caller.Can(UsersPermissions.TokensRead))
            throw Denied(UsersPermissions.TokensRead);

        var tokens = await _auth.ListApiTokensAsync(listAll ? null : caller.User.Id);
        var names = new Dictionary<string, string> { [caller.User.Id] = caller.User.Name };
        foreach (var t in tokens)
        {
            if (!names.ContainsKey(t.UserId))
                names[t.UserId] = (await _userStore.GetUserAsync(t.UserId))?.Name ?? t.UserId;
        }
        return JsonOk(tokens.Select(t => AuthService.ToApiTokenView(t, names[t.UserId])).ToList());
    }

    /// <summary>Create a token for the caller. Requires a session (403 with an API token).</summary>
    [HttpPost]
    [UsersCredentialEndpoint]
    public async Task<IActionResult> Create([FromBody] CreateApiTokenRequest request)
    {
        var caller = Auth;
        var (token, apiToken) = await _auth.CreateApiTokenAsync(caller, request);
        return Json(201, new CreateApiTokenResponse
        {
            Token = token,
            ApiToken = AuthService.ToApiTokenView(apiToken, caller.User.Name),
        });
    }

    /// <summary>Revoke a token: the owner, or a caller with <c>tokens:revoke</c>.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Revoke([FromRoute] string id)
    {
        var caller = Auth;
        await _auth.RevokeApiTokenAsync(caller, id, caller.Can(UsersPermissions.TokensRevoke));
        return NoContent();
    }
}
