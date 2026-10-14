using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Users.Controllers;

/// <summary>
/// Sessions resource:
/// <list type="bullet">
/// <item><c>POST   /sessions</c> — login: create a session from credentials (no auth)</item>
/// <item><c>GET    /sessions/current</c> — current session</item>
/// <item><c>DELETE /sessions/current</c> — logout: end the current session</item>
/// <item><c>GET    /sessions</c> — list active sessions (admin)</item>
/// <item><c>DELETE /sessions/{id}</c> — kick a session (admin)</item>
/// </list>
/// When an <see cref="IExternalAuthAdapter"/> is registered, login and logout
/// return 501 because authentication is managed by the host application.
/// </summary>
[ApiController]
[Route("api/v1/qcli/sessions")]
public class SessionsController : ControllerBase
{
    private readonly ISessionStore _sessionStore;
    private readonly ICliEventSocketManager _eventSocketManager;
    private readonly AuthService _authService;
    private readonly IExternalAuthAdapter? _authAdapter;

    public SessionsController(
        ISessionStore sessionStore,
        ICliEventSocketManager eventSocketManager,
        AuthService authService,
        IExternalAuthAdapter? authAdapter = null)
    {
        _sessionStore = sessionStore;
        _eventSocketManager = eventSocketManager;
        _authService = authService;
        _authAdapter = authAdapter;
    }

    /// <summary>
    /// Login: authenticate credentials and create a session. No auth required.
    /// Returns 201 with <c>{ token, session }</c>.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (_authAdapter != null)
            return StatusCode(501, new { error = "Authentication is managed by the host application" });

        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
            return BadRequest(new { error = "username and password are required" });

        try
        {
            var metadata = new SessionMetadata
            {
                RemoteAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = HttpContext.Request.Headers.UserAgent.FirstOrDefault(),
            };
            var result = await _authService.LoginAsync(request.Username, request.Password, metadata);
            return StatusCode(201, result);
        }
        catch (InvalidOperationException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Logout: end the current session.
    /// </summary>
    [HttpDelete("current")]
    public async Task<IActionResult> Logout()
    {
        if (_authAdapter != null)
            return StatusCode(501, new { error = "Authentication is managed by the host application" });

        var session = HttpContext.GetUsersSession();
        if (session == null)
            return Unauthorized(new { error = "Unauthorized" });

        var authHeader = HttpContext.Request.Headers.Authorization.FirstOrDefault() ?? "";
        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : "";

        await _authService.LogoutAsync(token);
        return NoContent();
    }

    /// <summary>
    /// List all active sessions (admin only).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        if (!HttpContext.IsUsersAdmin())
            return StatusCode(403, new { error = "Forbidden: admin access required" });

        return Ok(await _sessionStore.GetActiveSessionsAsync());
    }

    /// <summary>
    /// Get the current session.
    /// </summary>
    [HttpGet("current")]
    public IActionResult GetCurrent()
    {
        var session = HttpContext.GetUsersSession();
        if (session == null)
            return Unauthorized(new { error = "Unauthorized" });
        return Ok(session);
    }

    /// <summary>
    /// Kick/force-logout a session by id (admin only). Broadcasts a WebSocket event.
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Kick(string id)
    {
        if (!HttpContext.IsUsersAdmin())
            return StatusCode(403, new { error = "Forbidden: admin access required" });

        var session = await _sessionStore.GetSessionByIdAsync(id);
        if (session == null)
            return NotFound(new { error = "Session not found" });

        await _sessionStore.DeleteSessionByIdAsync(id);

        var eventMessage = JsonSerializer.Serialize(new
        {
            type = "session-kicked",
            data = new { sessionId = session.Id, userId = session.UserId },
        });
        await _eventSocketManager.BroadcastMessageAsync(eventMessage);

        return NoContent();
    }
}
