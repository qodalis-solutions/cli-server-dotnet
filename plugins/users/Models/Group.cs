using System.Text.Json.Serialization;

namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>
/// Represents a user group (role). Members receive the group's <see cref="Permissions"/>.
/// </summary>
public class Group
{
    /// <summary>Name of the built-in administrator group (permissions <c>["*"]</c>).</summary>
    public const string AdminGroup = "admin";

    /// <summary>Name of the built-in default group for new accounts (no permissions).</summary>
    public const string UsersGroup = "users";

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = [];

    [JsonPropertyName("builtIn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool BuiltIn { get; set; }

    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long UpdatedAt { get; set; }

    /// <summary>Returns a copy that does not share mutable collections with this instance.</summary>
    public Group Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        Permissions = [.. Permissions],
        BuiltIn = BuiltIn,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };

    /// <summary>True for the built-in <c>admin</c> and <c>users</c> groups (by flag or by name).</summary>
    [JsonIgnore]
    public bool IsBuiltIn =>
        BuiltIn ||
        string.Equals(Name, AdminGroup, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Name, UsersGroup, StringComparison.OrdinalIgnoreCase);
}
