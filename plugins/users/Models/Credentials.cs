namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>
/// Stores hashed password credentials for a user.
/// </summary>
public class Credentials
{
    public string UserId { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Salt { get; set; } = string.Empty;
    public long LastChanged { get; set; }
}
