using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

/// <summary>
/// Admin controller that reports server status including uptime, memory usage, and enabled features.
/// </summary>
[ApiController]
[Route("api/v1/qcli/status")]
public class StatusController : ControllerBase
{
    private readonly ICliEventSocketManager _eventSocketManager;
    private readonly ICliCommandRegistry _commandRegistry;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusController"/> class.
    /// </summary>
    public StatusController(ICliEventSocketManager eventSocketManager, ICliCommandRegistry commandRegistry, IServiceProvider serviceProvider)
    {
        _eventSocketManager = eventSocketManager;
        _commandRegistry = commandRegistry;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Returns the current server status including uptime, memory, connections, and enabled features.
    /// </summary>
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
            enabledFeatures = DetectEnabledFeatures(),
        });
    }

    private List<string> DetectEnabledFeatures()
    {
        var features = new List<string>();

        if (IsServiceRegistered("Qodalis.Cli.Plugin.FileSystem.IFileStorageProvider"))
            features.Add("filesystem");

        if (IsServiceRegistered("Qodalis.Cli.Plugin.Jobs.CliJobScheduler"))
            features.Add("jobs");

        return features;
    }

    private bool IsServiceRegistered(string fullTypeName)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a =>
            {
                try { return a.GetType(fullTypeName); }
                catch { return null; }
            })
            .FirstOrDefault(t => t != null);

        return type != null && _serviceProvider.GetService(type) != null;
    }
}
