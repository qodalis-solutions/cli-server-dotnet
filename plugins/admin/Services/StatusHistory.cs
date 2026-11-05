using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>One sample of the server status (<c>GET /api/v1/qcli/status/history</c>).</summary>
/// <param name="Timestamp">When it was taken (UTC).</param>
/// <param name="MemoryMb">The process working set in MB.</param>
/// <param name="CpuPercent">Process CPU over the previous interval as a percentage of one core; null for the first sample or when unknown.</param>
/// <param name="ActiveWsConnections">Connected event WebSocket clients.</param>
/// <param name="ActiveShellSessions">Open shell sessions.</param>
public sealed record StatusSample(
    DateTime Timestamp, double MemoryMb, double? CpuPercent, int ActiveWsConnections, int ActiveShellSessions);

/// <summary>
/// Samples the values of <c>GET /api/v1/qcli/status</c> every <see cref="Interval"/> (the first at start) into a ring
/// buffer of <see cref="Capacity"/> samples (24 hours), in memory. A hosted service: it stops with the host and does
/// not keep the process alive.
/// </summary>
public sealed class StatusHistory : BackgroundService
{
    /// <summary>The time between samples.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    /// <summary>Samples kept by default: 24 hours of them.</summary>
    public const int DefaultCapacity = 5760;

    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan?> _cpuTime;
    private readonly Func<double> _memoryMb;
    private readonly StatusSample?[] _buffer;
    private readonly object _lock = new();
    private long _written;
    private (DateTime At, TimeSpan Cpu)? _previous;

    /// <summary>Creates the sampler of the host's services.</summary>
    public StatusHistory(IServiceProvider services) : this(services, DefaultCapacity) { }

    /// <summary>Creates a sampler; the clock, CPU time and memory readers are replaceable for tests.</summary>
    internal StatusHistory(
        IServiceProvider services, int capacity, TimeProvider? time = null, Func<TimeSpan?>? cpuTime = null, Func<double>? memoryMb = null)
    {
        _services = services;
        _time = time ?? TimeProvider.System;
        _cpuTime = cpuTime ?? ProcessCpuTime;
        _memoryMb = memoryMb ?? ProcessMemoryMb;
        Capacity = Math.Max(1, capacity);
        _buffer = new StatusSample?[Capacity];
    }

    /// <summary>The most samples kept; older ones are overwritten.</summary>
    public int Capacity { get; }

    /// <summary>Takes a sample now and keeps it.</summary>
    public StatusSample TakeSample()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var cpu = _cpuTime();
        double? cpuPercent = null;
        lock (_lock)
        {
            if (cpu is { } used && _previous is { } previous && now > previous.At)
            {
                var percent = (used - previous.Cpu).TotalMilliseconds / (now - previous.At).TotalMilliseconds * 100;
                cpuPercent = Math.Round(Math.Max(0, percent), 1);
            }
            _previous = cpu is { } c ? (now, c) : null;
        }

        var sample = new StatusSample(
            now,
            Math.Round(_memoryMb(), 2),
            cpuPercent,
            _services.GetService<ICliEventSocketManager>()?.GetClients().Count ?? 0,
            _services.GetService<ICliShellSessions>()?.Count ?? 0);
        Add(sample);
        return sample;
    }

    /// <summary>Keeps <paramref name="sample"/>, overwriting the oldest when full.</summary>
    internal void Add(StatusSample sample)
    {
        lock (_lock)
        {
            _buffer[_written % Capacity] = sample;
            _written++;
        }
    }

    /// <summary>The samples of the last <paramref name="minutes"/> minutes, oldest first.</summary>
    public IReadOnlyList<StatusSample> GetSamples(int minutes)
    {
        var since = _time.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(minutes);
        lock (_lock)
        {
            var count = (int)Math.Min(_written, Capacity);
            var samples = new List<StatusSample>(count);
            for (var i = _written - count; i < _written; i++)
            {
                if (_buffer[i % Capacity] is { } sample && sample.Timestamp >= since) samples.Add(sample);
            }
            return samples;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        do
        {
            try
            {
                TakeSample();
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                // A failed sample leaves a gap in the chart; the next one may succeed.
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static TimeSpan? ProcessCpuTime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static double ProcessMemoryMb()
    {
        using var process = Process.GetCurrentProcess();
        return process.WorkingSet64 / (1024.0 * 1024.0);
    }
}
