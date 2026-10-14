using System.Text.Json.Serialization;

namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>
/// A stored session. References the user by id; the live user is resolved on every request.
/// Only the SHA-256 hash of the bearer token is stored. Never returned by the API directly —
/// see <see cref="SessionInfo"/> and <see cref="CurrentSession"/>.
/// </summary>
public class Session
{
    public string Id { get; set; } = string.Empty;
    /// <summary>Lower-case hex SHA-256 of the bearer token (the raw token is never stored).</summary>
    public string TokenHash { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;

    /// <summary>User name at login time (the API resolves the live name when available).</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Creation (login) time, epoch milliseconds.</summary>
    public long LoginTime { get; set; }

    /// <summary>Last activity time, epoch milliseconds. Slides on every authenticated request.</summary>
    public long LastActivity { get; set; }

    public SessionMetadata Metadata { get; set; } = new();

    public Session Clone() => new()
    {
        Id = Id,
        TokenHash = TokenHash,
        UserId = UserId,
        UserName = UserName,
        LoginTime = LoginTime,
        LastActivity = LastActivity,
        Metadata = new SessionMetadata
        {
            RemoteAddress = Metadata.RemoteAddress,
            UserAgent = Metadata.UserAgent,
        },
    };
}

/// <summary>
/// Metadata about a session's origin.
/// </summary>
public class SessionMetadata
{
    [JsonPropertyName("userAgent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserAgent { get; set; }

    [JsonPropertyName("remoteAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteAddress { get; set; }
}

/// <summary>
/// Session as returned by <c>GET /sessions</c> (listing). Never includes the token.
/// </summary>
public class SessionInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long CreatedAt { get; set; }

    [JsonPropertyName("lastActivity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long LastActivity { get; set; }

    [JsonPropertyName("expiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long ExpiresAt { get; set; }

    [JsonPropertyName("metadata")]
    public SessionMetadata Metadata { get; set; } = new();

    [JsonPropertyName("current")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool Current { get; set; }
}

/// <summary>
/// Session as returned by login (<c>session</c>) and <c>GET /sessions/current</c>.
/// </summary>
public class CurrentSession
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("user")]
    public User User { get; set; } = new();

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = [];

    [JsonPropertyName("loginTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long LoginTime { get; set; }

    [JsonPropertyName("lastActivity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long LastActivity { get; set; }

    [JsonPropertyName("expiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long ExpiresAt { get; set; }
}
