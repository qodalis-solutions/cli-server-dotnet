namespace Qodalis.Cli.Plugin.Users.EfCore;

/// <summary>A row of the <c>api_tokens</c> table: an API token without the token itself (its SHA-256 only).</summary>
public class ApiTokenEntity
{
    /// <summary>The token's id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The id of the user the token belongs to.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The token's name, unique per user (case-insensitive, checked by the users plugin).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Creation time, epoch milliseconds.</summary>
    public long CreatedAt { get; set; }

    /// <summary>Expiry, epoch milliseconds; null when the token never expires.</summary>
    public long? ExpiresAt { get; set; }

    /// <summary>Last use, epoch milliseconds; null when never used.</summary>
    public long? LastUsedAt { get; set; }

    /// <summary>The token's scope as a JSON array; null for the user's own permissions.</summary>
    public string? Permissions { get; set; }
}
