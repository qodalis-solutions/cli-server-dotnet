using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// Authentication core: password hashing, login/logout, lockout, request authentication
/// (idle timeout + live user resolution), effective permissions, session revocation
/// (with <c>session-kicked</c> broadcast) and audit logging.
/// </summary>
public class AuthService
{
    private readonly IUserStore _userStore;
    private readonly ISessionStore _sessionStore;
    private readonly ICredentialStore _credentialStore;
    private readonly IAuditStore _auditStore;
    private readonly IGroupStore _groupStore;
    private readonly UsersModuleOptions _options;
    private readonly ICliEventSocketManager? _events;
    private readonly TimeProvider _time;
    private readonly ILogger<AuthService> _logger;
    private readonly ConcurrentDictionary<string, LockoutState> _lockouts = new();

    public const string AccountLockedMessage = "account locked, try again later";
    public const string InvalidCredentialsMessage = "invalid credentials";
    public const string AccountDisabledMessage = "account disabled";

    /// <summary>Reasons sent in <c>session-kicked</c> broadcasts.</summary>
    public static class RevokeReasons
    {
        public const string Revoked = "revoked";
        public const string UserDeleted = "user_deleted";
        public const string UserDisabled = "user_disabled";
        public const string PasswordChanged = "password_changed";
    }

    private sealed class LockoutState
    {
        public int Failures;
        public long LockedUntil;
    }

    public AuthService(
        IUserStore userStore,
        ISessionStore sessionStore,
        ICredentialStore credentialStore,
        IAuditStore auditStore,
        IGroupStore groupStore,
        UsersModuleOptions? options = null,
        ICliEventSocketManager? eventSocketManager = null,
        TimeProvider? timeProvider = null,
        ILogger<AuthService>? logger = null)
    {
        _userStore = userStore;
        _sessionStore = sessionStore;
        _credentialStore = credentialStore;
        _auditStore = auditStore;
        _groupStore = groupStore;
        _options = options ?? new UsersModuleOptions();
        _events = eventSocketManager;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AuthService>.Instance;
    }

    public UsersModuleOptions Options => _options;

    /// <summary>Current time in epoch milliseconds (from the configured <see cref="TimeProvider"/>).</summary>
    public long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public long SessionTimeoutMs => _options.EffectiveSessionTimeoutMs;

    // -------------------------------------------------------------------------
    // Passwords
    // -------------------------------------------------------------------------

    /// <summary>Generates a cryptographically secure opaque bearer token.</summary>
    public string GenerateToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>Throws 400 when the password violates the policy.</summary>
    public void ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
            throw UsersApiException.BadRequest("password is required");
        if (password.Length < _options.MinPasswordLength)
            throw UsersApiException.BadRequest($"password must be at least {_options.MinPasswordLength} characters");
    }

    /// <summary>Hashes and stores the password for a user (no policy check).</summary>
    public async Task SetPasswordAsync(string userId, string password)
    {
        var (encoded, salt) = PasswordHasher.Hash(password);
        await _credentialStore.SetPasswordAsync(userId, encoded, salt);
    }

    /// <summary>Verifies a password against the stored credentials (constant-time).</summary>
    public async Task<bool> VerifyPasswordAsync(string userId, string password)
    {
        var creds = await _credentialStore.GetCredentialsAsync(userId);
        if (creds == null) return false;

        var valid = PasswordHasher.Verify(password, creds.PasswordHash, creds.Salt);
        if (valid && PasswordHasher.NeedsRehash(creds.PasswordHash))
            await SetPasswordAsync(userId, password);
        return valid;
    }

    // -------------------------------------------------------------------------
    // Lockout
    // -------------------------------------------------------------------------

    public bool IsLocked(string userId) =>
        _lockouts.TryGetValue(userId, out var state) && state.LockedUntil > Now();

    private void RegisterFailure(string userId)
    {
        var state = _lockouts.GetOrAdd(userId, _ => new LockoutState());
        lock (state)
        {
            var now = Now();
            if (state.LockedUntil != 0 && state.LockedUntil <= now)
            {
                // Previous lockout expired: start counting again.
                state.LockedUntil = 0;
                state.Failures = 0;
            }
            state.Failures++;
            if (state.Failures >= Math.Max(1, _options.MaxLoginAttempts))
            {
                state.LockedUntil = now + _options.LockoutMs;
                state.Failures = 0;
            }
        }
    }

    private void ResetFailures(string userId) => _lockouts.TryRemove(userId, out _);

    /// <summary>Clears lockout state for a user (e.g. when the user is deleted).</summary>
    public void ClearLockout(string userId) => ResetFailures(userId);

    // -------------------------------------------------------------------------
    // Permissions
    // -------------------------------------------------------------------------

    /// <summary>Effective permissions: union of the permissions of the user's groups.</summary>
    public async Task<List<string>> GetEffectivePermissionsAsync(User user)
    {
        var lists = new List<IEnumerable<string>?>();
        foreach (var groupName in user.Groups.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var group = await _groupStore.GetGroupAsync(groupName);
            lists.Add(group?.Permissions);
        }
        return PermissionMatcher.Merge(lists);
    }

    // -------------------------------------------------------------------------
    // Login / logout
    // -------------------------------------------------------------------------

    /// <summary>Finds a user by name, then by email (both case-insensitive).</summary>
    public async Task<User?> FindByNameOrEmailAsync(string nameOrEmail)
    {
        var users = await _userStore.GetUsersAsync();
        return users.FirstOrDefault(u => string.Equals(u.Name, nameOrEmail, StringComparison.OrdinalIgnoreCase))
            ?? users.FirstOrDefault(u => string.Equals(u.Email, nameOrEmail, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Authenticates by name or email and creates a session.
    /// Throws <see cref="UsersApiException"/>: 401 invalid credentials, 403 disabled, 429 locked.
    /// </summary>
    public async Task<LoginResponse> LoginAsync(string username, string password, SessionMetadata? metadata = null)
    {
        metadata ??= new SessionMetadata();
        var auditMeta = new AuditMetadata { RemoteAddress = metadata.RemoteAddress };

        var user = await FindByNameOrEmailAsync(username);
        if (user == null)
        {
            await AuditAsync(AuditEvents.LoginFailed, username, "user not found", metadata: auditMeta);
            throw UsersApiException.Unauthorized(InvalidCredentialsMessage);
        }

        if (IsLocked(user.Id))
        {
            await AuditAsync(AuditEvents.LoginFailed, user.Name, "account locked", metadata: auditMeta);
            throw new UsersApiException(429, AccountLockedMessage);
        }

        if (!await VerifyPasswordAsync(user.Id, password))
        {
            RegisterFailure(user.Id);
            await AuditAsync(AuditEvents.LoginFailed, user.Name, "wrong password", metadata: auditMeta);
            throw UsersApiException.Unauthorized(InvalidCredentialsMessage);
        }

        if (user.Disabled)
        {
            await AuditAsync(AuditEvents.LoginFailed, user.Name, "account disabled", metadata: auditMeta);
            throw UsersApiException.Forbidden(AccountDisabledMessage);
        }

        ResetFailures(user.Id);

        var now = Now();
        user = await _userStore.UpdateUserAsync(user.Id, new UpdateUserDto { LastLoginAt = now });

        var session = await _sessionStore.CreateSessionAsync(new Session
        {
            Id = Guid.NewGuid().ToString(),
            Token = GenerateToken(),
            UserId = user.Id,
            UserName = user.Name,
            LoginTime = now,
            LastActivity = now,
            Metadata = metadata,
        });

        await AuditAsync(AuditEvents.Login, user.Name, "login successful",
            metadata: new AuditMetadata { RemoteAddress = metadata.RemoteAddress, SessionId = session.Id });

        var permissions = await GetEffectivePermissionsAsync(user);
        return new LoginResponse
        {
            Token = session.Token,
            Session = new CurrentSession
            {
                Id = session.Id,
                User = user,
                Permissions = permissions,
                LoginTime = session.LoginTime,
                LastActivity = session.LastActivity,
                ExpiresAt = session.LastActivity + SessionTimeoutMs,
            },
        };
    }

    /// <summary>Ends the session associated with the given token.</summary>
    public async Task LogoutAsync(string token)
    {
        var session = await _sessionStore.GetSessionAsync(token);
        if (session == null) return;

        await _sessionStore.DeleteSessionAsync(token);
        await AuditAsync(AuditEvents.Logout, session.UserName, "user logged out",
            actor: session.UserName, metadata: new AuditMetadata { SessionId = session.Id });
    }

    /// <summary>
    /// Verifies a user's password for <c>verify-password</c>: honours lockout (429),
    /// counts failures toward it and audits failures.
    /// </summary>
    public async Task<bool> VerifyPasswordWithLockoutAsync(User user, string password, string? actor)
    {
        if (IsLocked(user.Id))
            throw new UsersApiException(429, AccountLockedMessage);

        var valid = await VerifyPasswordAsync(user.Id, password);
        if (valid)
        {
            ResetFailures(user.Id);
        }
        else
        {
            RegisterFailure(user.Id);
            await AuditAsync(AuditEvents.LoginFailed, user.Name, "password verification failed", actor);
        }
        return valid;
    }

    // -------------------------------------------------------------------------
    // Request authentication
    // -------------------------------------------------------------------------

    /// <summary>
    /// Authenticates a bearer token: checks idle expiry (expired sessions are deleted),
    /// resolves the live user (missing or disabled → 401), slides <c>lastActivity</c>.
    /// Throws <see cref="UsersApiException"/> (401) on failure.
    /// </summary>
    public async Task<UsersAuthContext> AuthenticateTokenAsync(string token)
    {
        var session = await _sessionStore.GetSessionAsync(token)
            ?? throw UsersApiException.Unauthorized("invalid or expired token");

        var now = Now();
        if (now - session.LastActivity > SessionTimeoutMs)
        {
            await _sessionStore.DeleteSessionAsync(token);
            throw UsersApiException.Unauthorized("session expired");
        }

        var user = await _userStore.GetUserAsync(session.UserId);
        if (user == null || user.Id != session.UserId)
        {
            await _sessionStore.DeleteSessionAsync(token);
            throw UsersApiException.Unauthorized("invalid or expired token");
        }

        if (user.Disabled)
            throw UsersApiException.Unauthorized(AccountDisabledMessage);

        await _sessionStore.UpdateLastActivityAsync(token, now);
        session.LastActivity = now;

        return new UsersAuthContext
        {
            User = user,
            Permissions = await GetEffectivePermissionsAsync(user),
            Session = session,
            SessionId = session.Id,
            LoginTime = session.LoginTime,
            LastActivity = now,
            ExpiresAt = now + SessionTimeoutMs,
        };
    }

    /// <summary>Builds the auth context for a request authenticated by an external adapter.</summary>
    public async Task<UsersAuthContext> AuthenticateExternalAsync(ExternalAuthResult result)
    {
        var source = result.User;
        var groups = new List<string>(source.Groups ?? []);
        if (result.IsAdmin && !groups.Any(g => string.Equals(g, Group.AdminGroup, StringComparison.OrdinalIgnoreCase)))
            groups.Add(Group.AdminGroup);

        var user = source.Clone();
        user.Groups = groups;

        var now = Now();
        return new UsersAuthContext
        {
            User = user,
            Permissions = await GetEffectivePermissionsAsync(user),
            Session = null,
            SessionId = $"external-{user.Id}",
            LoginTime = now,
            LastActivity = now,
            ExpiresAt = now + SessionTimeoutMs,
            IsExternal = true,
        };
    }

    // -------------------------------------------------------------------------
    // Sessions
    // -------------------------------------------------------------------------

    /// <summary>Listing view of a stored session (never includes the token).</summary>
    public SessionInfo ToSessionInfo(Session session, string? userName, string? currentSessionId) => new()
    {
        Id = session.Id,
        UserId = session.UserId,
        UserName = userName ?? session.UserName,
        CreatedAt = session.LoginTime,
        LastActivity = session.LastActivity,
        ExpiresAt = session.LastActivity + SessionTimeoutMs,
        Metadata = new SessionMetadata
        {
            UserAgent = session.Metadata?.UserAgent,
            RemoteAddress = session.Metadata?.RemoteAddress,
        },
        Current = currentSessionId != null && session.Id == currentSessionId,
    };

    /// <summary>
    /// Revokes a session: deletes it, audits <c>session_revoked</c> and broadcasts
    /// <c>session-kicked</c> with the given reason.
    /// </summary>
    public async Task RevokeSessionAsync(Session session, string reason, string? actor)
    {
        await _sessionStore.DeleteSessionByIdAsync(session.Id);
        await AuditAsync(AuditEvents.SessionRevoked, session.UserName, reason, actor,
            new AuditMetadata { SessionId = session.Id });
        await BroadcastSessionKickedAsync(session, reason);
    }

    /// <summary>Revokes all sessions of a user, optionally keeping one.</summary>
    public async Task RevokeUserSessionsAsync(string userId, string reason, string? actor, string? exceptSessionId = null)
    {
        var sessions = await _sessionStore.GetUserSessionsAsync(userId);
        foreach (var session in sessions.Where(s => s.Id != exceptSessionId))
            await RevokeSessionAsync(session, reason, actor);
    }

    private async Task BroadcastSessionKickedAsync(Session session, string reason)
    {
        if (_events == null) return;
        try
        {
            var message = JsonSerializer.Serialize(new
            {
                type = "session-kicked",
                data = new { sessionId = session.Id, userId = session.UserId, reason },
            });
            await _events.BroadcastMessageAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast session-kicked for session {SessionId}", session.Id);
        }
    }

    /// <summary>Deletes sessions idle for longer than the session timeout.</summary>
    public Task SweepIdleSessionsAsync() =>
        _sessionStore.DeleteIdleSessionsAsync(Now() - SessionTimeoutMs);

    // -------------------------------------------------------------------------
    // Audit
    // -------------------------------------------------------------------------

    public Task AuditAsync(string @event, string username, string? details = null, string? actor = null,
        AuditMetadata? metadata = null) =>
        _auditStore.LogAsync(new AuditEntry
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = Now(),
            Event = @event,
            Username = username,
            Details = details,
            Actor = actor,
            Metadata = metadata,
        });
}
