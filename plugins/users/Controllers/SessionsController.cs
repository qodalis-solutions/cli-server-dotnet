using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.Controllers;

/// <summary>
/// Sessions resource:
/// <list type="bullet">
/// <item><c>POST   /sessions</c> — login (anonymous, rate limited per IP) → 201 <c>{ token, session }</c></item>
/// <item><c>POST   /sessions/ws-ticket</c> — single-use WebSocket ticket (30 s)</item>
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
    private readonly UsersService _users;
    private readonly IpRateLimiter _limiter;

    public SessionsController(ISessionStore sessionStore, IUserStore userStore, AuthService auth,
        UsersService users, IpRateLimiter limiter)
    {
        _sessionStore = sessionStore;
        _userStore = userStore;
        _auth = auth;
        _users = users;
        _limiter = limiter;
    }

    /// <summary>Login by name or email.</summary>
    [HttpPost]
    [UsersAnonymous]
    [UsersCredentialEndpoint]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
            throw UsersApiException.BadRequest("username and password are required");
        if (request.Username.Length > UsersValidation.MaxEmailLength || request.Password.Length > _auth.Options.MaxPasswordLength)
            throw UsersApiException.BadRequest("username or password is too long");

        _limiter.Acquire(HttpContext);

        var metadata = new SessionMetadata
        {
            RemoteAddress = _limiter.ClientIp(HttpContext),
            UserAgent = UsersValidation.Sanitize(HttpContext.Request.Headers.UserAgent.FirstOrDefault(), 512),
        };
        return Json(201, await _auth.LoginAsync(request.Username, request.Password, metadata));
    }

    /// <summary>Issue a single-use WebSocket ticket bound to the current session (expires after 30 s).</summary>
    [HttpPost("ws-ticket")]
    public IActionResult CreateWsTicket() => Json(201, _auth.IssueTicket(Auth));

    /// <summary>The current session (live user and effective permissions).</summary>
    [HttpGet("current")]
    [UsersAllowPasswordChangeRequired]
    public IActionResult GetCurrent() => JsonOk(Auth.ToCurrentSession());

    /// <summary>Logout: end the current session.</summary>
    [HttpDelete("current")]
    [UsersCredentialEndpoint]
    [UsersAllowPasswordChangeRequired]
    public async Task<IActionResult> Logout()
    {
        var session = Auth.Session;
        if (session != null)
            await _auth.LogoutAsync(session);
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

    /// <summary>
    /// Revoke a session. Requires <c>sessions:revoke</c> (and covering the owner's permissions),
    /// or the session must be the caller's.
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Revoke([FromRoute] string id)
    {
        var caller = Auth;
        var privileged = caller.Can(UsersPermissions.SessionsRevoke);
        var session = await _sessionStore.GetSessionByIdAsync(id);

        if (session == null)
            throw privileged ? UsersApiException.NotFound("session not found") : Denied(UsersPermissions.SessionsRevoke);

        var own = session.UserId == caller.User.Id;
        if (!privileged && !own)
            throw Denied(UsersPermissions.SessionsRevoke);
        if (!own)
            await _users.EnsureCoversTargetAsync(caller, session.UserId);

        await _auth.RevokeSessionAsync(session, AuthService.RevokeReasons.Revoked, caller.User.Name, caller.User.Id);
        return NoContent();
    }
}
