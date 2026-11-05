namespace Qodalis.Cli.Abstractions.Audit;

/// <summary>
/// Options for querying audit entries.
/// </summary>
public class AuditQueryOptions
{
    /// <summary>The event (exact, case-insensitive), e.g. <c>login_failed</c>.</summary>
    public string? Event { get; set; }

    /// <summary>The user the events are about (exact, case-insensitive).</summary>
    public string? Username { get; set; }

    /// <summary>The user who performed the actions (exact, case-insensitive).</summary>
    public string? Actor { get; set; }

    /// <summary>Entries written at or after this time; null for no lower bound.</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>Entries written before this time; null for no upper bound.</summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>The most entries to return; null for all.</summary>
    public int? Limit { get; set; }

    /// <summary>Entries to skip, for paging (newest first).</summary>
    public int? Offset { get; set; }
}
