using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Admin.Services;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

/// <summary>
/// Admin controller for querying recent server log entries.
/// </summary>
[ApiController]
[Route("api/v1/qcli/logs")]
public class LogsController : ControllerBase
{
    private readonly LogRingBuffer _logBuffer;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogsController"/> class.
    /// </summary>
    public LogsController(LogRingBuffer logBuffer)
    {
        _logBuffer = logBuffer;
    }

    /// <summary>
    /// Queries recent log entries with optional filtering by level and search text.
    /// </summary>
    /// <param name="level">Optional log level filter (e.g., "INFO", "ERROR").</param>
    /// <param name="search">Optional text to search in messages and sources.</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <param name="offset">Number of entries to skip for pagination.</param>
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
            entries = result.Items.Select(e => new
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
