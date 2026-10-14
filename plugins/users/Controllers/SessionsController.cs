using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.Controllers;

/// <summary>
/// Sessions resource:
/// <list type="bullet">
/// <item><c>POST   /sessions</c> — login (anonymous) → 201 <c>{ token, session }</c></item>
/// <item><c>GET    /sessions/current</c> — current session</item>
/// <item><c>DELETE /sessions/current</c> — logout</item>
/// <item><c>GET    /sessions</c> — list sessions (<c>sessions:read</c>; never includes tokens)</item>
/// <item><c>DELETE /sessions/{id}</c> — revoke (<c>sessions:revoke</c> or own session)</item>
/// </list>
/// Login and logout return 501 when an external auth adapter is configured.
/// </summary>
[Route("api/v1/qcli/sessions")]
public class SessionsController : UsersControllerBase
{
    private readonly ISessionStore _sessionStore;
    private readonly IUserStore _userStore;
    private readonly AuthService _auth;

    public SessionsController(ISessionStore sessionStore, IUserStore userStore, AuthService auth)
    {
        _sessionStore = sessionStore;
        _userStore = userStore;
        _auth = auth;
    }

    /// <summary>Login by name or email.</summary>
    [HttpPost]
    [UsersAnonymous]
    [UsersCredentialEndpoint]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
            throw UsersApiException.BadRequest("username and password are required");

        var metadata = new SessionMetadata
        {
            RemoteAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = HttpContext.Request.Headers.UserAgent.FirstOrDefault(),
        };
        return Json(201, await _auth.LoginAsync(request.Username, request.Password, metadata));
    }

    /// <summary>The current session (live user and effective permissions).</summary>
    [HttpGet("current")]
    public IActionResult GetCurrent() => JsonOk(Auth.ToCurrentSession());

    /// <summary>Logout: end the current session.</summary>
    [HttpDelete("current")]
    [UsersCredentialEndpoint]
    public async Task<IActionResult> Logout()
    {
        var session = Auth.Session;
        if (session != null)
            await _auth.LogoutAsync(session.Token);
        return NoContent();
    }

    /// <summary>List active sessions. Requires <c>sessions:read</c>.</summary>
    [HttpGet]
    [UsersPermission(UsersPermissions.SessionsRead)]
    public async Task<IActionResult> GetAll()
    {
        var currentId = Auth.Session?.Id;
        var sessions = await _sessionStore.GetActiveSessionsAsync();
        var names = (await _userStore.GetUsersAsync()).ToDictionary(u => u.Id, u => u.Name);
        return JsonOk(sessions
            .Select(s => _auth.ToSessionInfo(s, names.GetValueOrDefault(s.UserId), currentId))
            .ToList());
    }

    /// <summary>Revoke a session. Requires <c>sessions:revoke</c>, or the session must be the caller's.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Revoke([FromRoute] string id)
    {
        var caller = Auth;
        var privileged = caller.Can(UsersPermissions.SessionsRevoke);
        var session = await _sessionStore.GetSessionByIdAsync(id);

        if (session == null)
            throw privileged ? UsersApiException.NotFound("session not found") : Denied(UsersPermissions.SessionsRevoke);
        if (!privileged && session.UserId != caller.User.Id)
            throw Denied(UsersPermissions.SessionsRevoke);

        await _auth.RevokeSessionAsync(session, AuthService.RevokeReasons.Revoked, caller.User.Name);
        return NoContent();
    }
}
