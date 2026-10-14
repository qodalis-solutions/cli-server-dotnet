using System.Text.Json.Serialization;

namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>
/// Represents a user in the system.
/// Optional fields are omitted from JSON when absent; <see cref="Disabled"/> is always written.
/// </summary>
public class User
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    /// <summary>Group names the user belongs to.</summary>
    [JsonPropertyName("groups")]
    public List<string> Groups { get; set; } = [];

    [JsonPropertyName("disabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool Disabled { get; set; }

    /// <summary>When true, the user's sessions may only change their own password.</summary>
    [JsonPropertyName("mustChangePassword")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool MustChangePassword { get; set; }

    [JsonPropertyName("homeDir")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HomeDir { get; set; }

    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long UpdatedAt { get; set; }

    [JsonPropertyName("lastLoginAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LastLoginAt { get; set; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Data { get; set; }

    /// <summary>Returns a copy that does not share mutable collections with this instance.</summary>
    public User Clone() => new()
    {
        Id = Id,
        Name = Name,
        Email = Email,
        Groups = [.. Groups],
        Disabled = Disabled,
        MustChangePassword = MustChangePassword,
        HomeDir = HomeDir,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        LastLoginAt = LastLoginAt,
        Data = Data == null ? null : new Dictionary<string, object>(Data),
    };

    /// <summary>True when the user is a member of <paramref name="group"/> (case-insensitive).</summary>
    public bool IsInGroup(string group) =>
        Groups.Any(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
}
