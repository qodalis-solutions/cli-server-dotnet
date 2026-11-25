using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.Admin.Services;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

/// <summary>
/// The pages plugins add to the dashboard (the dashboard's <c>docs/extensions.md</c>). Every page is listed; the
/// dashboard hides those the user may not see and each plugin's own API enforces access. Read on every request, so
/// pages added and removed at runtime (the marketplace's plugins, marked <c>source: marketplace</c>) show at once.
/// </summary>
[ApiController]
[Route("api/v1/qcli/dashboard")]
public class DashboardExtensionsController(CliDashboardExtensions extensions, DashboardMarketplace? marketplace = null) : ControllerBase
{
    /// <summary>The registered extensions, in registration order.</summary>
    [HttpGet("extensions")]
    public IActionResult List()
    {
        Response.Headers.CacheControl = "no-cache";
        var all = extensions.All;
        if (marketplace == null || !all.Any(e => marketplace.HasExtension(e.Id))) return Ok(new { extensions = all });
        return Ok(new
        {
            extensions = all.Select(e =>
            {
                if (!marketplace.HasExtension(e.Id)) return (object)e;
                var node = JsonSerializer.SerializeToNode(e)!.AsObject();
                node["source"] = "marketplace";
                return node;
            }).ToList(),
        });
    }
}
