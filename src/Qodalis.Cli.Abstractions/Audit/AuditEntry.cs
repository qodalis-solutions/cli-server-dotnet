using System.Text.Json.Serialization;

namespace Qodalis.Cli.Abstractions.Audit;

/// <summary>
/// An entry of the server's audit log (audit plugin): who did what, to whom or what, and when. The users plugin
/// records sign-ins, token and group changes; a host records its own changes through <see cref="IAuditStore"/>.
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

    /// <summary>
    /// What the event is about when it is not a user: a group (<c>group:admins</c>), a job, or a host's own
    /// object such as a role or plan.
    /// </summary>
    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; set; }

    /// <summary>
    /// The host's own values for this entry (e.g. the old and new value of a setting), saved with it and returned
    /// by the audit API. Values must serialize to JSON. Read back from a database store, the values are
    /// <see cref="System.Text.Json.JsonElement"/>s (the in-memory store keeps the objects).
    /// </summary>
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Properties { get; set; }

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

    /// <summary>Token events: id of the API token.</summary>
    [JsonPropertyName("tokenId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenId { get; set; }

    /// <summary>Token events: name of the API token.</summary>
    [JsonPropertyName("tokenName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenName { get; set; }

    /// <summary>Link events: the terminal's name (<c>link_approved</c>, <c>link_denied</c>).</summary>
    [JsonPropertyName("client")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Client { get; set; }

    /// <summary>Link events: the <c>Origin</c> of the terminal's link request, when it sent one.</summary>
    [JsonPropertyName("origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Origin { get; set; }
}
