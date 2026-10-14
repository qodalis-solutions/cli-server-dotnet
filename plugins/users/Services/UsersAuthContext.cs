using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// The authenticated principal for a users-plugin request, set by
/// <see cref="Middleware.UsersAuthMiddleware"/>. Controllers fail closed (401) without it.
/// </summary>
public sealed class UsersAuthContext
{
    internal const string ItemKey = "Qodalis.Cli.Plugin.Users.AuthContext";

    /// <summary>The live user (resolved from the store, or from the external adapter).</summary>
    public required User User { get; init; }

    /// <summary>Effective permissions (union of the user's groups' permissions).</summary>
    public required List<string> Permissions { get; init; }

    /// <summary>The stored session; <c>null</c> for external-auth requests.</summary>
    public Session? Session { get; init; }

    public required string SessionId { get; init; }
    public long LoginTime { get; init; }
    public long LastActivity { get; init; }
    public long ExpiresAt { get; init; }

    /// <summary>True when authenticated by an <see cref="IExternalAuthAdapter"/>.</summary>
    public bool IsExternal { get; init; }

    /// <summary>The adapter result (external mode), used to bind WebSocket tickets.</summary>
    public ExternalAuthResult? ExternalResult { get; init; }

    public bool Can(string permission) => PermissionMatcher.HasPermission(Permissions, permission);

    public bool IsSelf(User other) => other.Id == User.Id;

    /// <summary>The server-wide principal shared with core and other plugins.</summary>
    public Qodalis.Cli.Security.CliPrincipal ToCliPrincipal() =>
        new(User.Id, User.Name, Permissions, Session?.Id ?? SessionId);

    public CurrentSession ToCurrentSession() => new()
    {
        Id = SessionId,
        User = User,
        Permissions = [.. Permissions],
        LoginTime = LoginTime,
        LastActivity = LastActivity,
        ExpiresAt = ExpiresAt,
    };
}
