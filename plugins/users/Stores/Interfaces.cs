using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Stores;

/// <summary>
/// Storage interface for user records. Validation (duplicates, protections,
/// group references) is performed by the plugin before the store is called.
/// </summary>
public interface IUserStore
{
    Task<List<User>> GetUsersAsync();

    /// <summary>Finds a user by id, or by name (case-insensitive).</summary>
    Task<User?> GetUserAsync(string idOrName);

    Task<User> CreateUserAsync(CreateUserDto data);

    /// <summary>Applies the non-null fields of <paramref name="data"/>; throws when the user does not exist.</summary>
    Task<User> UpdateUserAsync(string id, UpdateUserDto data);

    Task DeleteUserAsync(string id);
}

/// <summary>
/// Storage interface for active sessions. Sessions are keyed by their bearer token.
/// </summary>
public interface ISessionStore
{
    /// <summary>Persists a fully populated session (id, token, user, timestamps).</summary>
    Task<Session> CreateSessionAsync(Session session);

    Task<Session?> GetSessionAsync(string token);
    Task<Session?> GetSessionByIdAsync(string id);
    Task<List<Session>> GetActiveSessionsAsync();
    Task<List<Session>> GetUserSessionsAsync(string userId);
    Task DeleteSessionAsync(string token);
    Task DeleteSessionByIdAsync(string id);
    Task DeleteUserSessionsAsync(string userId);

    /// <summary>Sets the session's last activity to <paramref name="timestamp"/> (epoch ms).</summary>
    Task UpdateLastActivityAsync(string token, long timestamp);

    /// <summary>Deletes sessions whose last activity is before <paramref name="lastActivityBefore"/> (epoch ms).</summary>
    Task DeleteIdleSessionsAsync(long lastActivityBefore);
}

/// <summary>
/// Storage interface for user credentials.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Stores the password hash for a user. <paramref name="hash"/> is the full encoded
    /// hash (<c>pbkdf2$iterations$salt$hash</c>); <paramref name="salt"/> is informational.
    /// </summary>
    Task SetPasswordAsync(string userId, string hash, string salt);
    Task<Credentials?> GetCredentialsAsync(string userId);
    Task DeleteCredentialsAsync(string userId);
}

/// <summary>
/// Storage interface for audit log entries.
/// </summary>
public interface IAuditStore
{
    Task LogAsync(AuditEntry entry);

    /// <summary>Returns entries newest first, filtered by <paramref name="options"/>.</summary>
    Task<List<AuditEntry>> GetEntriesAsync(AuditQueryOptions? options = null);
    Task ClearAsync();
}

/// <summary>
/// Storage interface for user groups. Group names are case-insensitive keys, stored as given.
/// </summary>
public interface IGroupStore
{
    Task<List<Group>> GetGroupsAsync();

    /// <summary>Finds a group by name (case-insensitive).</summary>
    Task<Group?> GetGroupAsync(string name);

    /// <summary>Creates a group; throws <see cref="InvalidOperationException"/> when the name exists.</summary>
    Task<Group> CreateGroupAsync(Group group);

    /// <summary>Applies the non-null fields of <paramref name="data"/>; throws when the group does not exist.</summary>
    Task<Group> UpdateGroupAsync(string name, UpdateGroupDto data);

    Task DeleteGroupAsync(string name);
}
