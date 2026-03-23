using Microsoft.AspNetCore.Mvc;

namespace Qodalis.Cli.Controllers;

/// <summary>
/// Controller for API version discovery, reporting supported and preferred API versions.
/// </summary>
[ApiController]
[Route("api/qcli")]
public class CliVersionController : ControllerBase
{
    /// <summary>
    /// Returns the supported API versions and the preferred version.
    /// </summary>
    [HttpGet("versions")]
    public IActionResult GetVersions()
    {
        return Ok(new
        {
            SupportedVersions = new[] { 1 },
            PreferredVersion = 1,
            ServerVersion = "1.0.0",
        });
    }
}
