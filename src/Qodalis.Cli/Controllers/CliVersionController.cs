using Microsoft.AspNetCore.Mvc;

namespace Qodalis.Cli.Controllers;

[ApiController]
[Route("api/cli")]
public class CliVersionController : ControllerBase
{
    [HttpGet("versions")]
    public IActionResult GetVersions()
    {
        return Ok(new
        {
            SupportedVersions = new[] { 1, 2 },
            PreferredVersion = 2,
            ServerVersion = "2.0.0",
        });
    }
}
