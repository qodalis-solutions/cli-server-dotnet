using Microsoft.AspNetCore.Mvc;

namespace Qodalis.Cli.Controllers;

[ApiController]
[Route("api/cli")]
public class CliController : ControllerBase
{
    [HttpGet("version")]
    public IActionResult GetVersion(CancellationToken cancellationToken)
    {
        var version = new
        {
            Version = "1.0.0"
        };

        return Ok(version);
    }

    [HttpGet("commands")]
    public async Task<IActionResult> GetCommandsAsync(CancellationToken cancellationToken)
    {
        return Ok();
    }
}