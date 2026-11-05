namespace Qodalis.Cli.Abstractions.Logs;

/// <summary>
/// Where the dashboard's Logs page reads its history from (<c>GET /api/v1/qcli/logs</c>). The admin plugin keeps the
/// latest 1000 entries of this process in memory; <c>Qodalis.Cli.Plugin.Logs.Seq</c> reads them from Seq, which keeps
/// them across restarts and for every service that writes there.
/// </summary>
public interface ILogStore
{
    /// <summary>A short name for the dashboard, e.g. <c>memory</c> or <c>seq</c>.</summary>
    string Name { get; }

    /// <summary>Whether entries outlive the process (and so are worth filtering by time).</summary>
    bool Persistent { get; }

    /// <summary>Returns entries newest first, filtered by <paramref name="options"/>.</summary>
    Task<LogPage> QueryAsync(LogQueryOptions options, CancellationToken cancellationToken = default);

    /// <summary>The services whose entries the store holds; empty when it holds only this process's.</summary>
    Task<IReadOnlyList<string>> GetServicesAsync(CancellationToken cancellationToken = default);
}
