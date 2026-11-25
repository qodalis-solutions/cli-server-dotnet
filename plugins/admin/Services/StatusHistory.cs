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

    /// <summary>Default minutes of <c>GET /status/history</c> and <c>admin status --history</c>.</summary>
    public const int DefaultHistoryMinutes = 60;

    /// <summary>Most minutes of <c>GET /status/history</c> and <c>admin status --history</c>.</summary>
    public const int MaxHistoryMinutes = 1440;

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

    /// <summary>The latest sample, or <c>null</c> before the first.</summary>
    public StatusSample? Latest
    {
        get
        {
            lock (_lock) return _written == 0 ? null : _buffer[(_written - 1) % Capacity];
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

/// <summary>Smallest, average and largest of a series (average rounded to 0.01, as memory is).</summary>
public sealed record StatusRange(double Min, double Avg, double Max);

/// <summary>A summary of status samples (<c>admin status --history</c>).</summary>
public sealed class StatusHistorySummary
{
    public int Count { get; init; }

    /// <summary>First sample time; <c>null</c> without samples.</summary>
    public string? From { get; init; }

    /// <summary>Last sample time; <c>null</c> without samples.</summary>
    public string? To { get; init; }

    public StatusRange? MemoryMb { get; init; }

    /// <summary>Over the samples that have a CPU value; <c>null</c> when none has.</summary>
    public StatusRange? CpuPercent { get; init; }

    /// <summary>Largest number of WebSocket connections seen.</summary>
    public int MaxWsConnections { get; init; }

    /// <summary>Largest number of shell sessions seen.</summary>
    public int MaxShellSessions { get; init; }

    /// <summary>Count, time span, min/avg/max memory and CPU, and the peak connections of <paramref name="samples"/>.</summary>
    public static StatusHistorySummary Of(IReadOnlyList<StatusSample> samples) => new()
    {
        Count = samples.Count,
        From = samples.Count > 0 ? Qodalis.Cli.Abstractions.Insights.TimeBuckets.Format(samples[0].Timestamp) : null,
        To = samples.Count > 0 ? Qodalis.Cli.Abstractions.Insights.TimeBuckets.Format(samples[^1].Timestamp) : null,
        MemoryMb = Range(samples.Select(s => s.MemoryMb).ToList()),
        CpuPercent = Range(samples.Where(s => s.CpuPercent != null).Select(s => s.CpuPercent!.Value).ToList()),
        MaxWsConnections = samples.Count > 0 ? samples.Max(s => s.ActiveWsConnections) : 0,
        MaxShellSessions = samples.Count > 0 ? samples.Max(s => s.ActiveShellSessions) : 0,
    };

    private static StatusRange? Range(List<double> values) => values.Count == 0
        ? null
        : new StatusRange(values.Min(), Math.Round(values.Average(), 2, MidpointRounding.AwayFromZero), values.Max());
}
