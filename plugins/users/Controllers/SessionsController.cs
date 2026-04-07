using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Users.Controllers;

/// <summary>
/// Session management endpoints: list, current, kick.
/// </summary>
[ApiController]
[Route("api/v1/qcli/sessions")]
public class SessionsController : ControllerBase
{
    private readonly ISessionStore _sessionStore;
    private readonly ICliEventSocketManager _eventSocketManager;

    public SessionsController(ISessionStore sessionStore, ICliEventSocketManager eventSocketManager)
    {
        _sessionStore = sessionStore;
        _eventSocketManager = eventSocketManager;
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
