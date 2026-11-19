using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>
/// Who an API token is for. <see cref="Cli"/> tokens use the CLI server's routes (commands, MCP, files, the
/// users routes, ...); <see cref="Host"/> tokens, created by the host application through
/// <c>IApiTokenManager</c>, are accepted only by the host's own guarded routes (<c>[QcliAuthorize]</c>).
/// </summary>
public static class ApiTokenAudience
{
    public const string Cli = "cli";
    public const string Host = "host";

    public static bool IsValid(string? value) => value is Cli or Host;
}

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

    private string _audience = ApiTokenAudience.Cli;

    /// <summary>
    /// <see cref="ApiTokenAudience.Cli"/> (default) or <see cref="ApiTokenAudience.Host"/>. A store that has no
    /// value (rows written before audiences existed, stores that drop unknown fields) gives <c>cli</c>.
    /// </summary>
    public string Audience
    {
        get => _audience;
        set => _audience = string.IsNullOrEmpty(value) ? ApiTokenAudience.Cli : value;
    }

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
        Audience = Audience,
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

    /// <summary><c>cli</c> or <c>host</c>; always written.</summary>
    [JsonPropertyName("audience")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string Audience { get; set; } = ApiTokenAudience.Cli;

    /// <summary>The scope; omitted when the token is unscoped.</summary>
    [JsonPropertyName("permissions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Permissions { get; set; }

    /// <summary>
    /// Requests made with the token over the last 30 days (UTC); set on listings only when an
    /// <see cref="Services.IApiTokenUsageProvider"/> is registered (the public-api plugin registers one), else omitted.
    /// </summary>
    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApiTokenUsage? Usage { get; set; }
}

/// <summary>A token's requests: today, the last 30 days, and one entry per day (oldest first, the last one today, UTC).</summary>
public sealed class ApiTokenUsage
{
    /// <summary>Requests today (UTC); equals the last entry of <see cref="Days"/>.</summary>
    [JsonPropertyName("today")]
    public long Today { get; init; }

    /// <summary>The sum of <see cref="Days"/>.</summary>
    [JsonPropertyName("last30Days")]
    public long Last30Days { get; init; }

    /// <summary>Exactly 30 entries, zeros included, oldest first.</summary>
    [JsonPropertyName("days")]
    public IReadOnlyList<ApiTokenUsageDay> Days { get; init; } = [];
}

/// <summary>Requests made with a token on one UTC day (<c>yyyy-MM-dd</c>).</summary>
public sealed record ApiTokenUsageDay(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("requests")] long Requests);

/// <summary>
/// Request body for <c>POST /tokens</c>: <c>{ name, expiresInDays?, permissions?, audience? }</c>.
/// <see cref="ExpiresInDays"/> is kept raw to tell an absent value (default 90 days) from <c>null</c> (never expires),
/// <see cref="Audience"/> to tell an absent value from <c>null</c> or a non-string.
/// </summary>
public class CreateApiTokenRequest
{
    public string? Name { get; set; }
    public JsonElement ExpiresInDays { get; set; }
    public List<string>? Permissions { get; set; }
    public JsonElement Audience { get; set; }
}

/// <summary>Response body for <c>POST /tokens</c>: <c>{ token, apiToken }</c>. The token is shown only once.</summary>
public class CreateApiTokenResponse
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("apiToken")]
    public ApiTokenView ApiToken { get; set; } = new();
}
