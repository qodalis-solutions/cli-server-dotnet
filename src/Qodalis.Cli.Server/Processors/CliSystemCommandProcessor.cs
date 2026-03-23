using System.Runtime.InteropServices;
using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Server.Processors;

/// <summary>
/// Command processor that displays detailed system information including hostname, OS, CPU, memory, and uptime.
/// </summary>
public class CliSystemCommandProcessor : CliCommandProcessor
{
    private static readonly DateTime StartTime = DateTime.UtcNow;

    public override string Command { get; set; } = "system";
    public override string Description { get; set; } = "Shows server system information";

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var uptime = DateTime.UtcNow - StartTime;
        var totalMem = (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0).ToString("F1");

        var lines = new[]
        {
            $"Hostname:      {Environment.MachineName}",
            $"OS:            {RuntimeInformation.OSDescription}",
            $"Architecture:  {RuntimeInformation.OSArchitecture}",
            $"CPU Cores:     {Environment.ProcessorCount}",
            $".NET:          {RuntimeInformation.FrameworkDescription}",
            $"Memory:        {totalMem} GB total",
            $"Server Uptime: {(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s",
        };

        return Task.FromResult(string.Join("\n", lines));
    }
}
