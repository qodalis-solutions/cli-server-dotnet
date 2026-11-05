using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

using Qodalis.Cli.Abstractions.Jobs;
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
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken = default)
    {
        var jobs = _serviceProvider.GetService<ICliJobManager>();
        var registeredJobs = jobs == null ? 0 : (await jobs.GetJobsAsync(cancellationToken)).Count;

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
            activeShellSessions = _serviceProvider.GetService(typeof(ICliShellSessions)) is ICliShellSessions shells ? shells.Count : 0,
            registeredCommands = _commandRegistry.Processors.Count,
            registeredJobs,
            enabledFeatures = DetectEnabledFeatures(jobs != null),
        });
    }

    private List<string> DetectEnabledFeatures(bool hasJobs)
    {
        var features = new List<string>();

        // AddFileSystem() registers its options; IFileStorageProvider alone is always there (in-memory fallback).
        if (_serviceProvider.GetService<FileSystemOptions>() != null)
            features.Add("filesystem");

        if (hasJobs)
            features.Add("jobs");

        return features;
    }
}
