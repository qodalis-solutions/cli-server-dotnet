using System.Diagnostics;
using System.Runtime.InteropServices;
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
    /// <summary>The audit plugin's package id (the admin plugin does not reference it).</summary>
    private const string AuditPluginId = "Qodalis.Cli.Plugin.Audit";

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

    /// <summary>
    /// The status sampled every 15 seconds over the last <paramref name="minutes"/> minutes (default 60, 1..1440),
    /// oldest first: memory, CPU, WebSocket clients and shell sessions. Kept in memory for 24 hours.
    /// </summary>
    /// <param name="minutes">How far back to go.</param>
    [HttpGet("history")]
    public IActionResult GetHistory([FromQuery] string? minutes = null)
    {
        var span = 60;
        if (!string.IsNullOrWhiteSpace(minutes)
            && !int.TryParse(minutes.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out span))
        {
            return BadRequest(new { error = "minutes must be an integer" });
        }
        span = Math.Clamp(span, 1, 1440);

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

    private List<string> DetectEnabledFeatures(bool hasJobs)
    {
        var features = new List<string>();

        // AddFileSystem() registers its options; IFileStorageProvider alone is always there (in-memory fallback).
        if (_serviceProvider.GetService<FileSystemOptions>() != null)
            features.Add("filesystem");

        if (hasJobs)
            features.Add("jobs");

        // The audit plugin serves the audit log (GET /api/v1/qcli/audit); the users plugin adds it.
        if (_serviceProvider.GetService<CliInstalledPlugins>()?.All.Any(p => p.Id == AuditPluginId) == true)
            features.Add("audit");

        return features;
    }
}
