namespace Qodalis.Cli.Abstractions.Audit;

/// <summary>
/// Where the audit log is kept (audit plugin). The default keeps the latest entries in memory; implement it to keep
/// the log elsewhere and register it with <c>cli.SetAuditStore&lt;T&gt;()</c> (or use the EF Core store,
/// <c>Qodalis.Cli.Plugin.Audit.EfCore</c>).
/// </summary>
public interface IAuditStore
{
    /// <summary>Records an entry; an empty <see cref="AuditEntry.Id"/> gets a new id.</summary>
    Task LogAsync(AuditEntry entry);

    /// <summary>Returns entries newest first, filtered by <paramref name="options"/>.</summary>
    Task<List<AuditEntry>> GetEntriesAsync(AuditQueryOptions? options = null);
    /// <summary>Deletes every entry.</summary>
    Task ClearAsync();

    /// <summary>
    /// Deletes the entries recorded before <paramref name="before"/>; returns how many. Run it on a schedule to keep
    /// the log bounded. The default keeps everything and returns 0.
    /// </summary>
    Task<int> PruneAsync(DateTimeOffset before, CancellationToken cancellationToken = default) => Task.FromResult(0);
}
