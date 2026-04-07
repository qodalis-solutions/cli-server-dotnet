namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>
/// Represents an active user session.
/// </summary>
public class Session
{
    public string Id { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = new();
    public long LoginTime { get; set; }
    public long LastActivity { get; set; }
    public SessionMetadata Metadata { get; set; } = new();
}

/// <summary>
/// Metadata about a session's origin.
/// </summary>
public class SessionMetadata
{
    public string? RemoteAddress { get; set; }
    public string? UserAgent { get; set; }
}
