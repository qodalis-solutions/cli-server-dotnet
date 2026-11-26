using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>The server status (<c>GET /api/v1/qcli/status</c> and <c>admin status</c>).</summary>
public sealed class StatusSnapshot
{
    [JsonPropertyName("uptimeSeconds")]
    public long UptimeSeconds { get; init; }
    /// <summary>The process working set in MB.</summary>
    [JsonPropertyName("memoryUsageMb")]
    public double MemoryUsageMb { get; init; }

    /// <summary>The RAM the server can use in MB (the container limit when one is set below the installed RAM); <c>null</c> when unknown.</summary>
    [JsonPropertyName("systemMemoryTotalMb")]
    public long? SystemMemoryTotalMb { get; init; }

    /// <summary>The memory in use in MB (the container's usage when limited, else the machine's); <c>null</c> when unknown.</summary>
    [JsonPropertyName("systemMemoryUsedMb")]
    public long? SystemMemoryUsedMb { get; init; }

    /// <summary><c>true</c> when <see cref="SystemMemoryTotalMb"/> is a container limit; <c>null</c> when unknown.</summary>
    [JsonPropertyName("memoryLimited")]
    public bool? MemoryLimited { get; init; }
    [JsonPropertyName("startedAt")]
    public required string StartedAt { get; init; }
    [JsonPropertyName("platform")]
    public required string Platform { get; init; }
    [JsonPropertyName("platformVersion")]
    public required string PlatformVersion { get; init; }
    [JsonPropertyName("os")]
    public required string Os { get; init; }
    [JsonPropertyName("activeWsConnections")]
    public int ActiveWsConnections { get; init; }
    [JsonPropertyName("activeShellSessions")]
    public int ActiveShellSessions { get; init; }
    [JsonPropertyName("registeredCommands")]
    public int RegisteredCommands { get; init; }
    [JsonPropertyName("registeredJobs")]
    public int RegisteredJobs { get; init; }
    [JsonPropertyName("enabledFeatures")]
    public required List<string> EnabledFeatures { get; init; }

    /// <summary>Process CPU of the latest status sample (percent of one core); <c>null</c> before the second sample.</summary>
    [JsonPropertyName("cpuUsagePercent")]
    public double? CpuUsagePercent { get; init; }
}

/// <summary>
/// Reads the server status now: process metrics, the machine's (or container's) memory, the latest CPU sample and the
/// server's counts.
/// </summary>
public sealed class ServerStatus(ICliEventSocketManager eventSocketManager, ICliCommandRegistry commandRegistry, IServiceProvider services)
{
    /// <summary>The audit plugin's package id (the admin plugin does not reference it).</summary>
    private const string AuditPluginId = "Qodalis.Cli.Plugin.Audit";

    /// <summary>The status now.</summary>
    public async Task<StatusSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        // A host may register its job manager as scoped (SetJobManager<T>()): this singleton holds the root provider,
        // so it reads the jobs in a scope of its own (resolving a scoped service from the root throws under scope validation)
        int registeredJobs;
        bool hasJobs;
        await using (var scope = services.CreateAsyncScope())
        {
            var jobs = scope.ServiceProvider.GetService<ICliJobManager>();
            hasJobs = jobs != null;
            registeredJobs = jobs == null ? 0 : (await jobs.GetJobsAsync(cancellationToken)).Count;
        }

        using var process = Process.GetCurrentProcess();
        var started = process.StartTime.ToUniversalTime();
        var system = SystemMemory.Read(services.GetService<SystemMemoryReaders>());
        return new StatusSnapshot
        {
            UptimeSeconds = (long)(DateTime.UtcNow - started).TotalSeconds,
            MemoryUsageMb = Math.Round(process.WorkingSet64 / (1024.0 * 1024.0), 2),
            SystemMemoryTotalMb = system.TotalMb,
            SystemMemoryUsedMb = system.UsedMb,
            MemoryLimited = system.Limited,
            StartedAt = started.ToString("o"),
            Platform = "dotnet",
            PlatformVersion = RuntimeInformation.FrameworkDescription,
            Os = RuntimeInformation.OSDescription,
            ActiveWsConnections = eventSocketManager.GetClients().Count,
            ActiveShellSessions = services.GetService<ICliShellSessions>()?.Count ?? 0,
            RegisteredCommands = commandRegistry.Processors.Count,
            RegisteredJobs = registeredJobs,
            EnabledFeatures = EnabledFeatures(hasJobs),
            CpuUsagePercent = services.GetService<StatusHistory>()?.Latest?.CpuPercent,
        };
    }

    private List<string> EnabledFeatures(bool hasJobs)
    {
        var features = new List<string>();

        // AddFileSystem() registers its options; IFileStorageProvider alone is always there (in-memory fallback).
        if (services.GetService<FileSystemOptions>() != null)
            features.Add("filesystem");

        if (hasJobs)
            features.Add("jobs");

        // The audit plugin serves the audit log (GET /api/v1/qcli/audit); the users plugin adds it.
        if (services.GetService<CliInstalledPlugins>()?.All.Any(p => p.Id == AuditPluginId) == true)
            features.Add("audit");

        return features;
    }
}
