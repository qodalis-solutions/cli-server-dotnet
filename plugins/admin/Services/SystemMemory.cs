using System.Globalization;
using System.Runtime.InteropServices;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>The machine's (or container's) memory, as <c>GET /api/v1/qcli/status</c> reports it. Every value is <c>null</c> when unknown.</summary>
/// <param name="TotalMb">The RAM the server can use in MB: the container limit when one is set below the installed RAM, else the installed RAM.</param>
/// <param name="UsedMb">The memory in use in MB: the container's usage when limited, else the machine's (total minus available).</param>
/// <param name="Limited"><c>true</c> when <paramref name="TotalMb"/> is a container (cgroup) limit.</param>
public sealed record SystemMemoryInfo(long? TotalMb, long? UsedMb, bool? Limited)
{
    /// <summary>Nothing known.</summary>
    public static readonly SystemMemoryInfo Unknown = new(null, null, null);
}

/// <summary>
/// Where <see cref="SystemMemory.Read"/> reads from, in bytes. Register another instance in the services to replace
/// them (tests). A reader may throw or return <c>null</c>.
/// </summary>
public sealed class SystemMemoryReaders
{
    /// <summary>Installed RAM: <c>MemTotal</c> of <c>/proc/meminfo</c> on Linux, <c>hw.memsize</c> on macOS, <c>GlobalMemoryStatusEx</c> on Windows.</summary>
    public required Func<long?> InstalledBytes { get; init; }

    /// <summary>
    /// Available RAM of the machine: <c>MemAvailable</c> on Linux, <c>GlobalMemoryStatusEx</c> on Windows, free +
    /// file-backed + purgeable + speculative pages on macOS.
    /// </summary>
    public required Func<long?> AvailableBytes { get; init; }

    /// <summary>
    /// The container limit: cgroup v2 <c>memory.max</c> (v1 <c>memory.limit_in_bytes</c>), else the GC's
    /// container-aware <c>TotalAvailableMemoryBytes</c> (not on macOS). A value at or above the installed RAM is no limit.
    /// </summary>
    public required Func<long?> LimitBytes { get; init; }

    /// <summary>The container's usage: cgroup v2 <c>memory.current</c> (v1 <c>memory.usage_in_bytes</c>).</summary>
    public required Func<long?> ContainerUsageBytes { get; init; }

    /// <summary>The readers of this process.</summary>
    public static SystemMemoryReaders Default { get; } = new()
    {
        InstalledBytes = SystemMemory.ReadInstalledBytes,
        AvailableBytes = SystemMemory.ReadAvailableBytes,
        LimitBytes = SystemMemory.ReadLimitBytes,
        ContainerUsageBytes = () => SystemMemory.ReadFirstNumber(SystemMemory.CgroupUsageFiles),
    };
}

/// <summary>Reads the RAM the server can use and the memory in use (<c>systemMemoryTotalMb</c>, <c>systemMemoryUsedMb</c>, <c>memoryLimited</c>).</summary>
public static class SystemMemory
{
    internal static readonly string[] CgroupLimitFiles = ["/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory/memory.limit_in_bytes"];
    internal static readonly string[] CgroupUsageFiles = ["/sys/fs/cgroup/memory.current", "/sys/fs/cgroup/memory/memory.usage_in_bytes"];

    /// <summary>The memory now; never throws (what cannot be read is <c>null</c>).</summary>
    public static SystemMemoryInfo Read(SystemMemoryReaders? readers = null)
    {
        var r = readers ?? SystemMemoryReaders.Default;
        var installed = Safe(r.InstalledBytes);
        var limit = Safe(r.LimitBytes);
        var limited = limit is { } l && installed is { } i && l < i;
        var total = limited ? limit : installed;
        if (total is not { } totalBytes) return SystemMemoryInfo.Unknown;

        long? used = null;
        if (limited)
        {
            used = Safe(r.ContainerUsageBytes);
        }
        else if (Safe(r.AvailableBytes) is { } available && available <= totalBytes)
        {
            used = totalBytes - available;
        }
        var totalMb = ToMb(totalBytes);
        return new SystemMemoryInfo(totalMb, used is { } u ? Math.Min(ToMb(u), totalMb) : null, limited);
    }

    private static long ToMb(long bytes) => (long)Math.Round(bytes / (1024.0 * 1024.0), MidpointRounding.AwayFromZero);

    private static long? Safe(Func<long?> read)
    {
        try
        {
            return read() is { } value && value > 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    internal static long? ReadInstalledBytes()
    {
        if (OperatingSystem.IsLinux()) return MemInfoBytes("MemTotal:");
        if (OperatingSystem.IsMacOS()) return SysctlLong("hw.memsize");
        if (OperatingSystem.IsWindows()) return WindowsMemoryStatus() is { } status ? (long)status.ullTotalPhys : null;
        return null;
    }

    internal static long? ReadAvailableBytes()
    {
        if (OperatingSystem.IsLinux()) return MemInfoBytes("MemAvailable:");
        if (OperatingSystem.IsWindows()) return WindowsMemoryStatus() is { } status ? (long)status.ullAvailPhys : null;
        if (OperatingSystem.IsMacOS())
        {
            // As psutil and Activity Monitor see it: free pages plus those the system reclaims without swapping.
            var pageSize = SysctlLong("vm.pagesize");
            long pages = 0;
            foreach (var name in new[] { "vm.page_free_count", "vm.page_pageable_external_count", "vm.page_purgeable_count", "vm.page_speculative_count" })
            {
                if (SysctlLong(name) is not { } count) return null;
                pages += count;
            }
            return pageSize is { } size ? pages * size : null;
        }
        return null;
    }

    internal static long? ReadLimitBytes()
    {
        if (OperatingSystem.IsLinux() && ReadFirstNumber(CgroupLimitFiles) is { } cgroup) return cgroup;
        if (OperatingSystem.IsMacOS()) return null;
        // Container-aware (Windows job objects, a cgroup the files do not show)
        var gc = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return gc > 0 ? gc : null;
    }

    /// <summary>The number in the first readable file; <c>null</c> for cgroup v2's <c>max</c> (no limit) or when none is readable.</summary>
    internal static long? ReadFirstNumber(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                continue;
            }
            if (text == "max") return null;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        }
        return null;
    }

    private static long? MemInfoBytes(string key)
    {
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (!line.StartsWith(key, StringComparison.Ordinal)) continue;
            var parts = line[key.Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) ? kb * 1024 : null;
        }
        return null;
    }

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(string name, ref long value, ref nint length, nint newValue, nint newLength);

    /// <summary>A numeric sysctl (32 or 64 bits) on macOS.</summary>
    private static long? SysctlLong(string name)
    {
        long value = 0;
        nint length = sizeof(long);
        if (SysctlByName(name, ref value, ref length, 0, 0) != 0) return null;
        return length switch
        {
            sizeof(long) => value,
            sizeof(int) => (uint)value, // little-endian: the low 32 bits
            _ => null,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static MemoryStatusEx? WindowsMemoryStatus()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status : null;
    }
}
