using Qodalis.Cli.Security;

namespace Qodalis.Cli.Services;

/// <summary>
/// Registry of open shell sessions: enforces the concurrency limits and lets authentication
/// plugins end the shells bound to a session or user (web-cli spec Appendix C2/C3).
/// </summary>
public interface ICliShellSessions
{
    /// <summary>Number of open shells.</summary>
    int Count { get; }

    /// <summary>Snapshot of the open shells.</summary>
    IReadOnlyList<CliShellSessionInfo> GetSessions();

    /// <summary>Closes (code 4401, <c>session ended</c>) every shell opened by the given authentication session.</summary>
    void CloseBySession(string sessionId, string reason = "session ended");

    /// <summary>Closes (code 4401) every shell opened by the given user.</summary>
    void CloseByUser(string userId, string reason = "session ended");

    /// <summary>
    /// Asks every session-bound shell to re-validate its session and <c>shell:execute</c> now
    /// (e.g. after a permission, group or user change).
    /// </summary>
    void RequestRecheck();
}

/// <summary>Public view of an open shell (never contains the command).</summary>
public sealed record CliShellSessionInfo(
    string Id,
    string Mode,
    string? UserId,
    string? UserName,
    string? AuthSessionId,
    string? RemoteAddress,
    DateTimeOffset StartedAt);

/// <summary>A registered shell; disposing it frees its slot.</summary>
public sealed class CliShellSessionHandle : IDisposable
{
    private readonly CliShellSessionRegistry _registry;
    private int _recheck;
    private int _disposed;

    internal CliShellSessionHandle(CliShellSessionRegistry registry, string key, string mode,
        CliPrincipal? principal, string? remoteAddress)
    {
        _registry = registry;
        Key = key;
        Mode = mode;
        Principal = principal;
        RemoteAddress = remoteAddress;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");
    internal string Key { get; }
    public string Mode { get; }
    public CliPrincipal? Principal { get; internal set; }
    public string? RemoteAddress { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Raised when the registry asks this shell to close (code, reason).</summary>
    internal Action<int, string>? OnTerminate { get; set; }

    internal void Terminate(int code, string reason) => OnTerminate?.Invoke(code, reason);

    internal void FlagRecheck() => Interlocked.Exchange(ref _recheck, 1);

    /// <summary>True (once) when a re-check was requested since the last call.</summary>
    internal bool TakeRecheck() => Interlocked.Exchange(ref _recheck, 0) == 1;

    internal CliShellSessionInfo ToInfo() => new(Id, Mode, Principal?.UserId, Principal?.UserName,
        Principal?.SessionId, RemoteAddress, StartedAt);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _registry.Remove(this);
    }
}

/// <summary>Default in-process <see cref="ICliShellSessions"/>.</summary>
public sealed class CliShellSessionRegistry : ICliShellSessions
{
    private readonly object _gate = new();
    private readonly List<CliShellSessionHandle> _sessions = [];

    public int Count
    {
        get { lock (_gate) return _sessions.Count; }
    }

    public IReadOnlyList<CliShellSessionInfo> GetSessions()
    {
        lock (_gate) return _sessions.Select(s => s.ToInfo()).ToList();
    }

    /// <summary>
    /// Reserves a slot for a new shell keyed by user (users mode) or remote address. Null when
    /// <paramref name="maxPerKey"/> or <paramref name="maxTotal"/> is reached (≤ 0 = unlimited).
    /// </summary>
    public CliShellSessionHandle? TryRegister(string key, int maxPerKey, int maxTotal, string mode,
        CliPrincipal? principal, string? remoteAddress)
    {
        lock (_gate)
        {
            if (maxTotal > 0 && _sessions.Count >= maxTotal) return null;
            if (maxPerKey > 0 && _sessions.Count(s => s.Key == key) >= maxPerKey) return null;
            var handle = new CliShellSessionHandle(this, key, mode, principal, remoteAddress);
            _sessions.Add(handle);
            return handle;
        }
    }

    internal void Remove(CliShellSessionHandle handle)
    {
        lock (_gate) _sessions.Remove(handle);
    }

    public void CloseBySession(string sessionId, string reason = "session ended") =>
        TerminateWhere(s => s.Principal?.SessionId == sessionId, reason);

    public void CloseByUser(string userId, string reason = "session ended") =>
        TerminateWhere(s => s.Principal?.UserId == userId, reason);

    public void RequestRecheck()
    {
        List<CliShellSessionHandle> bound;
        lock (_gate) bound = _sessions.Where(s => s.Principal != null).ToList();
        foreach (var s in bound) s.FlagRecheck();
    }

    private void TerminateWhere(Func<CliShellSessionHandle, bool> predicate, string reason)
    {
        List<CliShellSessionHandle> matches;
        lock (_gate) matches = _sessions.Where(predicate).ToList();
        foreach (var s in matches) s.Terminate(CliShellCloseCodes.Unauthorized, reason);
    }
}
