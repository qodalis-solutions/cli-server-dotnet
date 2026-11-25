using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

using Qodalis.Cli.Abstractions.Insights;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

/// <summary>
/// Admin controller that reports server status including uptime, memory usage, and enabled features.
/// </summary>
[ApiController]
[Route("api/v1/qcli/status")]
public class StatusController : ControllerBase
{
    private readonly ServerStatus _status;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusController"/> class.
    /// </summary>
    public StatusController(ICliEventSocketManager eventSocketManager, ICliCommandRegistry commandRegistry, IServiceProvider serviceProvider)
    {
        _status = serviceProvider.GetService<ServerStatus>() ?? new ServerStatus(eventSocketManager, commandRegistry, serviceProvider);
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Returns the current server status including uptime, memory, CPU (the latest sample), connections, and enabled
    /// features (the same as <c>admin status</c>).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken = default) =>
        Ok(await _status.SnapshotAsync(cancellationToken));

    /// <summary>
    /// The status sampled every 15 seconds over the last <paramref name="minutes"/> minutes (default 60, 1..1440),
    /// oldest first: memory, CPU, WebSocket clients and shell sessions. Kept in memory for 24 hours.
    /// </summary>
    /// <param name="minutes">How far back to go.</param>
    [HttpGet("history")]
    public IActionResult GetHistory([FromQuery] string? minutes = null)
    {
        var span = StatusHistory.DefaultHistoryMinutes;
        if (!string.IsNullOrWhiteSpace(minutes)
            && !int.TryParse(minutes.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out span))
        {
            return BadRequest(new { error = "minutes must be an integer" });
        }
        span = Math.Clamp(span, 1, StatusHistory.MaxHistoryMinutes);

        var samples = _serviceProvider.GetService<StatusHistory>()?.GetSamples(span) ?? [];
        return Ok(new
        {
            intervalSeconds = (int)StatusHistory.Interval.TotalSeconds,
            samples = samples.Select(s => new
            {
                timestamp = TimeBuckets.Format(s.Timestamp),
                memoryMb = s.MemoryMb,
                cpuPercent = s.CpuPercent,
                activeWsConnections = s.ActiveWsConnections,
                activeShellSessions = s.ActiveShellSessions,
            }),
        });
    }
}
