using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

[ApiController]
[Route("api/v1/qcli/status")]
public class StatusController : ControllerBase
{
    private readonly CliEventSocketManager _eventSocketManager;
    private readonly CliCommandRegistry _commandRegistry;

    public StatusController(CliEventSocketManager eventSocketManager, CliCommandRegistry commandRegistry)
    {
        _eventSocketManager = eventSocketManager;
        _commandRegistry = commandRegistry;
    }

    [HttpGet]
    public IActionResult GetStatus()
    {
        var process = Process.GetCurrentProcess();
        var uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime();

        return Ok(new
        {
            uptimeSeconds = (long)uptime.TotalSeconds,
            memoryUsageMb = Math.Round(process.WorkingSet64 / (1024.0 * 1024.0), 2),
            startedAt = process.StartTime.ToUniversalTime().ToString("o"),
            platform = "dotnet",
            platformVersion = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            activeWsConnections = _eventSocketManager.GetClients().Count,
            activeShellSessions = 0,
            registeredCommands = _commandRegistry.Processors.Count,
            registeredJobs = 0,
        });
    }
}
