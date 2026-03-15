using Microsoft.AspNetCore.Mvc;

namespace Qodalis.Cli.Controllers;

[ApiController]
[Route("api/qcli")]
public class CliVersionController : ControllerBase
{
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
