namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>
/// Represents a user in the system.
/// </summary>
public class User
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public List<string> Groups { get; set; } = [];
    public bool? Disabled { get; set; }
    public Dictionary<string, object>? Data { get; set; }
    public long? LastLoginAt { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}
