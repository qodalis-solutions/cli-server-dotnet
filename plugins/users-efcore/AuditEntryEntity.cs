namespace Qodalis.Cli.Plugin.Users.EfCore;

/// <summary>
/// EF Core entity for one audit log entry (<see cref="Models.AuditEntry"/>).
/// </summary>
internal class AuditEntryEntity
{
    /// <summary>Auto-incremented primary key; orders entries saved in the same millisecond.</summary>
    public long Id { get; set; }
    /// <summary>The entry's id (<see cref="Models.AuditEntry.Id"/>).</summary>
    public required string EntryId { get; set; }
    /// <summary>Unix time in milliseconds.</summary>
    public long Timestamp { get; set; }
    public required string Event { get; set; }
    public required string Username { get; set; }
    public string? UserId { get; set; }
    public string? Details { get; set; }
    public string? Actor { get; set; }
    public string? ActorId { get; set; }
    public string? Target { get; set; }
    /// <summary><see cref="Models.AuditEntry.Properties"/> as JSON.</summary>
    public string? Properties { get; set; }
    /// <summary><see cref="Models.AuditMetadata"/> as JSON.</summary>
    public string? Metadata { get; set; }
}
