using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Stores;

/// <summary>
/// Storage interface for user records.
/// </summary>
public interface IUserStore
{
    Task<List<User>> GetUsersAsync();
    Task<User?> GetUserAsync(string idOrName);
    Task<User> CreateUserAsync(CreateUserDto data);
    Task<User> UpdateUserAsync(string id, UpdateUserDto data);
    Task DeleteUserAsync(string id);
}

/// <summary>
/// Storage interface for active sessions.
/// </summary>
public interface ISessionStore
{
    Task<Session> CreateSessionAsync(string userId, string token, User user, SessionMetadata metadata);
    Task<Session?> GetSessionAsync(string token);
    Task<Session?> GetSessionByIdAsync(string id);
    Task<List<Session>> GetActiveSessionsAsync();
    Task DeleteSessionAsync(string token);
    Task DeleteSessionByIdAsync(string id);
    Task DeleteUserSessionsAsync(string userId);
    Task UpdateLastActivityAsync(string token);
    Task CleanupAsync(long maxAgeMs);
}

/// <summary>
/// Storage interface for user credentials.
/// </summary>
public interface ICredentialStore
{
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
    Task<List<AuditEntry>> GetEntriesAsync(AuditQueryOptions? options = null);
    Task ClearAsync();
}

/// <summary>
/// Storage interface for user groups.
/// </summary>
public interface IGroupStore
{
    Task<List<Group>> GetGroupsAsync();
    Task<Group?> GetGroupAsync(string name);
    Task<Group> CreateGroupAsync(string name, string? description = null);
    Task DeleteGroupAsync(string name);
}
