using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.Controllers;

/// <summary>
/// Audit log query and management endpoints (admin only).
/// </summary>
[ApiController]
[Route("api/v1/qcli/audit")]
public class AuditController : ControllerBase
{
    private readonly IAuditStore _auditStore;

    public AuditController(IAuditStore auditStore)
    {
        _auditStore = auditStore;
    }

    /// <summary>
    /// Query audit entries. Supports ?event=, ?username=, ?limit= query params.
    /// Admin only.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? @event,
        [FromQuery] string? username,
        [FromQuery] int? limit)
    {
        if (!HttpContext.IsUsersAdmin())
            return StatusCode(403, new { error = "Forbidden: admin access required" });

        var options = new AuditQueryOptions
        {
            Event = @event,
            Username = username,
            Limit = limit,
        };

        return Ok(await _auditStore.GetEntriesAsync(options));
    }

    /// <summary>
    /// Clear all audit entries. Admin only.
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> Clear()
    {
        if (!HttpContext.IsUsersAdmin())
            return StatusCode(403, new { error = "Forbidden: admin access required" });

        await _auditStore.ClearAsync();
        return NoContent();
    }
}
