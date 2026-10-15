using System.Text.Json.Serialization;

namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>
/// Represents an audit log entry.
/// </summary>
public class AuditEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long Timestamp { get; set; }

    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    /// <summary>The user the event is about.</summary>
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>Id of the user the event is about, when known.</summary>
    [JsonPropertyName("userId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserId { get; set; }

    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Details { get; set; }

    /// <summary>The user who performed the action, when applicable.</summary>
    [JsonPropertyName("actor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Actor { get; set; }

    /// <summary>Id of the acting user, when known.</summary>
    [JsonPropertyName("actorId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActorId { get; set; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AuditMetadata? Metadata { get; set; }
}

/// <summary>
/// Additional metadata for an audit entry.
/// </summary>
public class AuditMetadata
{
    [JsonPropertyName("remoteAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteAddress { get; set; }

    [JsonPropertyName("sessionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; set; }

    /// <summary>Shell events: <c>interactive</c> or <c>command</c> (the command text is never recorded).</summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    /// <summary>Shell events: lifetime of the shell in milliseconds (<c>shell_closed</c>).</summary>
    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; set; }

    /// <summary>Shell events: process exit code (<c>shell_closed</c>, when the process exited by itself).</summary>
    [JsonPropertyName("exitCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExitCode { get; set; }

    /// <summary>Shell events: why the shell closed (<c>shell_closed</c>).</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }
}

/// <summary>
/// Audit event names logged by the plugin.
/// </summary>
public static class AuditEvents
{
    public const string Login = "login";
    public const string LoginFailed = "login_failed";
    public const string Logout = "logout";
    public const string UserCreated = "user_created";
    public const string UserUpdated = "user_updated";
    public const string UserDeleted = "user_deleted";
    public const string UserDisabled = "user_disabled";
    public const string UserEnabled = "user_enabled";
    public const string PasswordChanged = "password_changed";
    public const string GroupCreated = "group_created";
    public const string GroupUpdated = "group_updated";
    public const string GroupDeleted = "group_deleted";
    public const string GroupMemberAdded = "group_member_added";
    public const string GroupMemberRemoved = "group_member_removed";
    public const string SessionRevoked = "session_revoked";
    public const string AuditCleared = "audit_cleared";
    public const string ShellOpened = "shell_opened";
    public const string ShellClosed = "shell_closed";
}
