namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>
/// Represents a user group.
/// </summary>
public class Group
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public long CreatedAt { get; set; }
}
