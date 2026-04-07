namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>
/// Represents an audit log entry.
/// </summary>
public class AuditEntry
{
    public long Timestamp { get; set; }
    public string Event { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? Details { get; set; }
    public AuditMetadata? Metadata { get; set; }
}

/// <summary>
/// Additional metadata for an audit entry.
/// </summary>
public class AuditMetadata
{
    public string? RemoteAddress { get; set; }
    public string? SessionId { get; set; }
}
