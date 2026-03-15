using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Admin.Services;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

[ApiController]
[Route("api/v1/qcli/logs")]
public class LogsController : ControllerBase
{
    private readonly LogRingBuffer _logBuffer;

    public LogsController(LogRingBuffer logBuffer)
    {
        _logBuffer = logBuffer;
    }

    [HttpGet]
    public IActionResult GetLogs(
        [FromQuery] string? level = null,
        [FromQuery] string? search = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0)
    {
        if (limit <= 0) limit = 100;
        if (offset < 0) offset = 0;

        var result = _logBuffer.Query(level, search, limit, offset);

        return Ok(new
        {
            items = result.Items.Select(e => new
            {
                timestamp = e.Timestamp.ToString("o"),
                level = e.Level,
                message = e.Message,
                source = e.Source,
            }),
            total = result.Total,
            limit = result.Limit,
            offset = result.Offset,
        });
    }
}
