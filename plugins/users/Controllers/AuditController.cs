using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Users.Services;

namespace Qodalis.Cli.Plugin.Users.Controllers;

/// <summary>
/// Audit log query and management endpoints.
/// </summary>
[Route("api/v1/qcli/audit")]
public class AuditController : UsersControllerBase
{
    private readonly UsersService _users;

    public AuditController(UsersService users)
    {
        _users = users;
    }

    /// <summary>Query audit entries (<c>?event=&amp;username=&amp;limit=</c>). Requires <c>audit:read</c>.</summary>
    [HttpGet]
    [UsersPermission(UsersPermissions.AuditRead)]
    public async Task<IActionResult> GetAll(
        [FromQuery(Name = "event")] string? @event,
        [FromQuery] string? username,
        [FromQuery] int? limit) =>
        JsonOk(await _users.GetAuditAsync(@event, username, limit));

    /// <summary>Clear the audit log (an <c>audit_cleared</c> entry is then recorded). Requires <c>audit:clear</c>.</summary>
    [HttpDelete]
    [UsersPermission(UsersPermissions.AuditClear)]
    public async Task<IActionResult> Clear()
    {
        await _users.ClearAuditAsync(Auth);
        return NoContent();
    }
}
