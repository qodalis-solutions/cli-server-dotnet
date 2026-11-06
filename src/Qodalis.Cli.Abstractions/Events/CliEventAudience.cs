namespace Qodalis.Cli.Abstractions.Events;

/// <summary>
/// Who receives an event over the events WebSocket: everyone (both null), the sockets whose principal has
/// <see cref="Permission"/>, the sockets authenticated as <see cref="UserId"/>, or (both set) either of them.
/// On a server without authentication every socket receives every event; in-process subscribers always do.
/// </summary>
/// <param name="Permission">A permission such as <c>jobs:read</c>.</param>
/// <param name="UserId">A user id.</param>
public sealed record CliEventAudience(string? Permission = null, string? UserId = null)
{
    /// <summary>Every connected events socket.</summary>
    public static CliEventAudience Everyone { get; } = new();

    /// <summary>The sockets whose principal has <paramref name="permission"/>.</summary>
    public static CliEventAudience WithPermission(string permission) => new(permission);

    /// <summary>The sockets authenticated as <paramref name="userId"/>.</summary>
    public static CliEventAudience User(string userId) => new(UserId: userId);

    /// <summary>True when the event goes to every socket.</summary>
    public bool IsEveryone => Permission == null && UserId == null;
}
