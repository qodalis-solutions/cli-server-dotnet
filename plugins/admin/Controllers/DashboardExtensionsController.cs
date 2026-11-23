using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Models;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

/// <summary>
/// The pages plugins add to the dashboard (the dashboard's <c>docs/extensions.md</c>). Every page is listed; the
/// dashboard hides those the user may not see and each plugin's own API enforces access.
/// </summary>
[ApiController]
[Route("api/v1/qcli/dashboard")]
public class DashboardExtensionsController(CliDashboardExtensions extensions) : ControllerBase
{
    /// <summary>The registered extensions, in registration order.</summary>
    [HttpGet("extensions")]
    public IActionResult List()
    {
        Response.Headers.CacheControl = "no-cache";
        return Ok(new { extensions = extensions.All });
    }
}
