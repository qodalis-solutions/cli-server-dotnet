using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Audit;

/// <summary>
/// Wraps the server's <see cref="IAuditStore"/> (the audit plugin registers it around whichever store is set) and
/// publishes <c>audit:recorded</c> (audience <c>audit:read</c>) with the entry, as the audit API returns it, after
/// every <see cref="LogAsync"/>: the users plugin's entries and the host's own alike. The entry's <c>timestamp</c> is
/// the event's (ISO 8601 on the wire).
/// </summary>
public sealed class CliEventAuditStore : IAuditStore
{
    /// <summary>The event type.</summary>
    public const string RecordedEvent = "audit:recorded";

    private static readonly CliEventAudience Audience = CliEventAudience.WithPermission(CliRoutePolicy.AuditRead);

    private readonly ICliEventBus? _events;

    /// <summary>Initializes a new instance of <see cref="CliEventAuditStore"/>.</summary>
    /// <param name="inner">The store that keeps the log.</param>
    /// <param name="events">The bus the events are published on; none when null.</param>
    public CliEventAuditStore(IAuditStore inner, ICliEventBus? events)
    {
        Inner = inner;
        _events = events;
    }

    /// <summary>The store that keeps the log.</summary>
    public IAuditStore Inner { get; }

    /// <summary>The store behind <paramref name="store"/> when it is a <see cref="CliEventAuditStore"/>.</summary>
    public static IAuditStore Unwrap(IAuditStore store) => store is CliEventAuditStore events ? events.Inner : store;

    /// <inheritdoc />
    public async Task LogAsync(AuditEntry entry)
    {
        await Inner.LogAsync(entry);
        if (_events != null) await _events.PublishAsync(RecordedEvent, entry, Audience);
    }

    /// <inheritdoc />
    public Task<List<AuditEntry>> GetEntriesAsync(AuditQueryOptions? options = null) => Inner.GetEntriesAsync(options);

    /// <inheritdoc />
    public Task ClearAsync() => Inner.ClearAsync();

    /// <inheritdoc />
    public Task<int> PruneAsync(DateTimeOffset before, CancellationToken cancellationToken = default) =>
        Inner.PruneAsync(before, cancellationToken);
}
