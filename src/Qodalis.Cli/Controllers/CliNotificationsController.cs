using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Abstractions.Notifications;
using Qodalis.Cli.Notifications;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Controllers;

/// <summary>
/// <c>/api/v1/qcli/notifications</c>: the caller's system notifications (any signed-in user on a protected server, each
/// seeing those whose permission they hold; open on a server without sign-in, one shared reader).
/// <list type="bullet">
/// <item><c>GET /?unread=true&amp;severity=&amp;limit=</c>: <c>{ items, unreadCount }</c>, newest first (400 for a bad severity or limit)</item>
/// <item><c>POST /read-all</c>: <c>{ marked, unreadCount }</c></item>
/// <item><c>POST /{id}/read</c>: <c>{ item, unreadCount }</c></item>
/// <item><c>DELETE /{id}</c>: dismisses it for the caller, <c>{ dismissed: true, unreadCount }</c>; <c>?scope=all</c> clears it for everyone (<c>notifications:manage</c>), <c>{ cleared: true, unreadCount }</c></item>
/// </list>
/// 404 <c>{ error: "notification not found" }</c> for one that does not exist or the caller cannot see.
/// </summary>
[ApiController]
[Route("api/v1/qcli/notifications")]
public class CliNotificationsController(CliNotificationService notifications, CliServerOptions options) : ControllerBase
{
    private const int MaxLimit = 500;

    private CliNotificationViewer Viewer => CliNotificationViewer.Of(HttpContext.GetCliPrincipal(), options);

    private ObjectResult NotFoundError() => StatusCode(404, new { error = "notification not found" });

    [HttpGet]
    public IActionResult List([FromQuery] string? unread = null, [FromQuery] string? severity = null, [FromQuery] string? limit = null)
    {
        if (!string.IsNullOrEmpty(severity) && !CliNotificationSeverities.All.Contains(severity))
            return StatusCode(400, new { error = $"invalid severity: {severity} (info, warning, error)" });
        int? max = null;
        if (!string.IsNullOrEmpty(limit))
        {
            if (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > MaxLimit)
                return StatusCode(400, new { error = $"limit must be an integer from 1 to {MaxLimit}" });
            max = n;
        }
        Response.Headers.CacheControl = "no-store";
        return Ok(notifications.List(Viewer, unread is "true" or "1", string.IsNullOrEmpty(severity) ? null : severity, max));
    }

    [HttpPost("read-all")]
    public IActionResult ReadAll()
    {
        var viewer = Viewer;
        var marked = notifications.MarkAllRead(viewer);
        return Ok(new { marked, unreadCount = notifications.UnreadCount(viewer) });
    }

    [HttpPost("{id}/read")]
    public IActionResult Read(string id)
    {
        var viewer = Viewer;
        var item = notifications.MarkRead(viewer, id);
        if (item == null) return NotFoundError();
        return Ok(new { item, unreadCount = notifications.UnreadCount(viewer) });
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id, [FromQuery] string? scope = null)
    {
        var viewer = Viewer;
        if (scope == "all")
        {
            if (!viewer.Can(CliNotificationConstants.ManagePermission))
                return StatusCode(403, new { error = $"forbidden: missing permission {CliNotificationConstants.ManagePermission}" });
            if (!notifications.Clear(viewer, id)) return NotFoundError();
            return Ok(new { cleared = true, unreadCount = notifications.UnreadCount(viewer) });
        }
        if (!notifications.Dismiss(viewer, id)) return NotFoundError();
        return Ok(new { dismissed = true, unreadCount = notifications.UnreadCount(viewer) });
    }
}
