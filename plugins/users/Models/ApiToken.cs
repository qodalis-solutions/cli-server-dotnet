using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>
/// A stored API token: a long-lived, revocable bearer token (<c>qcli_pat_…</c>) for MCP clients,
/// CI and scripts. Only the SHA-256 hash of the token is stored. Never returned by the API
/// directly — see <see cref="ApiTokenView"/>.
/// </summary>
public class ApiToken
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;

    /// <summary>1-64 characters of <c>[A-Za-z0-9._ -]</c>, unique per user (case-insensitive).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the token (same hashing as session tokens).</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Creation time, epoch milliseconds.</summary>
    public long CreatedAt { get; set; }

    /// <summary>Expiry, epoch milliseconds; <c>null</c> when the token never expires.</summary>
    public long? ExpiresAt { get; set; }

    /// <summary>Last use, epoch milliseconds (updated at most once per minute); <c>null</c> when never used.</summary>
    public long? LastUsedAt { get; set; }

    /// <summary>Optional scope; <c>null</c> = the user's full effective permissions.</summary>
    public List<string>? Permissions { get; set; }

    public bool IsExpired(long now) => ExpiresAt.HasValue && now >= ExpiresAt.Value;

    public ApiToken Clone() => new()
    {
        Id = Id,
        UserId = UserId,
        Name = Name,
        TokenHash = TokenHash,
        CreatedAt = CreatedAt,
        ExpiresAt = ExpiresAt,
        LastUsedAt = LastUsedAt,
        Permissions = Permissions == null ? null : [.. Permissions],
    };
}

/// <summary>
/// API token as returned by <c>GET /tokens</c> and <c>POST /tokens</c>. Never contains the token or its hash.
/// </summary>
public class ApiTokenView
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long CreatedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? ExpiresAt { get; set; }

    [JsonPropertyName("lastUsedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? LastUsedAt { get; set; }

    /// <summary>The scope; omitted when the token is unscoped.</summary>
    [JsonPropertyName("permissions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Permissions { get; set; }
}

/// <summary>
/// Request body for <c>POST /tokens</c>: <c>{ name, expiresInDays?, permissions? }</c>.
/// <see cref="ExpiresInDays"/> is kept raw to tell an absent value (default 90 days) from <c>null</c> (never expires).
/// </summary>
public class CreateApiTokenRequest
{
    public string? Name { get; set; }
    public JsonElement ExpiresInDays { get; set; }
    public List<string>? Permissions { get; set; }
}

/// <summary>Response body for <c>POST /tokens</c>: <c>{ token, apiToken }</c>. The token is shown only once.</summary>
public class CreateApiTokenResponse
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("apiToken")]
    public ApiTokenView ApiToken { get; set; } = new();
}
