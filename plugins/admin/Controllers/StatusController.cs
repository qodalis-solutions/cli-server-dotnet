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

    public StatusController(CliEventSocketManager eventSocketManager)
    {
        _eventSocketManager = eventSocketManager;
    }

    [HttpGet]
    public IActionResult GetStatus()
    {
        var process = Process.GetCurrentProcess();
        var uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime();

        return Ok(new
        {
            uptime = new
            {
                seconds = (long)uptime.TotalSeconds,
                formatted = FormatUptime(uptime),
            },
            memory = new
            {
                workingSetBytes = process.WorkingSet64,
                workingSetMb = Math.Round(process.WorkingSet64 / (1024.0 * 1024.0), 2),
                gcTotalMemoryBytes = GC.GetTotalMemory(false),
                gcTotalMemoryMb = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 2),
            },
            platform = "dotnet",
            platformVersion = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            connections = new
            {
                eventClients = _eventSocketManager.GetClients().Count,
            },
            startedAt = process.StartTime.ToUniversalTime().ToString("o"),
        });
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        if (uptime.TotalHours >= 1)
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s";
        if (uptime.TotalMinutes >= 1)
            return $"{(int)uptime.TotalMinutes}m {uptime.Seconds}s";
        return $"{uptime.Seconds}s";
    }
}
