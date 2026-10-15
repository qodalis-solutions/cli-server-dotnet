using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// Authentication core: password hashing (bounded concurrency), login/logout, atomic lockout,
/// request authentication (hashed tokens, idle + absolute expiry, live user resolution),
/// WebSocket tickets, effective permissions, session revocation (user-scoped
/// <c>session-kicked</c>) and audit logging.
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
    private readonly ICliShellSessions? _shells;
    private readonly ConcurrentDictionary<string, LockoutState> _lockouts = new();
    private readonly ConcurrentDictionary<string, WsTicket> _tickets = new();
    private readonly ConditionalWeakTable<CliPrincipal, ExternalRequestSnapshot> _externalRequests = new();
    private readonly SemaphoreSlim _hashGate;

    /// <summary>Tracked lockout entries above which expired ones are pruned.</summary>
    internal int MaxTrackedLockouts { get; init; } = 10_000;

    /// <summary>Number of tracked lockout entries (names and users).</summary>
    internal int TrackedLockouts => _lockouts.Count;

    private const int MaxOutstandingTickets = 10_000;

    /// <summary>WebSocket ticket lifetime in seconds.</summary>
    public const int TicketLifetimeSeconds = 30;

    public const string AccountLockedMessage = "account locked, try again later";
    public const string InvalidCredentialsMessage = "invalid credentials";
    public const string AccountDisabledMessage = "account disabled";
    public const string PasswordChangeRequiredMessage = "password change required";

    // Used to spend the same PBKDF2 time for unknown users.
    private static readonly string DummyHash = PasswordHasher.Hash(Guid.NewGuid().ToString()).Encoded;

    /// <summary>Reasons sent in <c>session-kicked</c> events.</summary>
    public static class RevokeReasons
    {
        public const string Revoked = "revoked";
        public const string UserDeleted = "user_deleted";
        public const string UserDisabled = "user_disabled";
        public const string PasswordChanged = "password_changed";
    }

    /// <summary>Failure count of one name or user. Mutated only under <c>lock (state)</c>.</summary>
    private sealed class LockoutState
    {
        public int Failures;
        public long LastFailure;
        public long LockedUntil;

        /// <summary>Detached from the table (pruned or reset): callers retry with a fresh entry.</summary>
        public bool Removed;
    }

    private sealed record WsTicket(string? SessionId, ExternalAuthResult? External, ExternalRequestSnapshot? ExternalRequest,
        long ExpiresAt);

    public AuthService(
        IUserStore userStore,
        ISessionStore sessionStore,
        ICredentialStore credentialStore,
        IAuditStore auditStore,
        IGroupStore groupStore,
        UsersModuleOptions? options = null,
        ICliEventSocketManager? eventSocketManager = null,
        TimeProvider? timeProvider = null,
        ILogger<AuthService>? logger = null,
        ICliShellSessions? shellSessions = null)
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
        _shells = shellSessions;
        _hashGate = new SemaphoreSlim(Math.Max(1, _options.PasswordHashConcurrency));
    }

    public UsersModuleOptions Options => _options;

    /// <summary>Current time in epoch milliseconds (from the configured <see cref="TimeProvider"/>).</summary>
    public long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public long SessionTimeoutMs => _options.EffectiveSessionTimeoutMs;

    public long MaxSessionLifetimeMs => _options.EffectiveMaxSessionLifetimeMs;

    /// <summary><c>min(lastActivity + idle timeout, loginTime + absolute lifetime)</c>.</summary>
    public long ExpiresAt(long loginTime, long lastActivity) =>
        Math.Min(lastActivity + SessionTimeoutMs, loginTime + MaxSessionLifetimeMs);

    // -------------------------------------------------------------------------
    // Tokens & passwords
    // -------------------------------------------------------------------------

    /// <summary>Generates a cryptographically secure opaque token (32 random bytes, hex).</summary>
    public string GenerateToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>Lower-case hex SHA-256 of a token; stores only ever see this.</summary>
    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Throws 400 when the password violates the policy.</summary>
    public void ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
            throw UsersApiException.BadRequest("password is required");
        if (password.Length < _options.MinPasswordLength)
            throw UsersApiException.BadRequest($"password must be at least {_options.MinPasswordLength} characters");
        if (password.Length > _options.MaxPasswordLength)
            throw UsersApiException.BadRequest($"password must be at most {_options.MaxPasswordLength} characters");
    }

    private async Task<T> HashWorkAsync<T>(Func<T> work)
    {
        await _hashGate.WaitAsync();
        try
        {
            return await Task.Run(work);
        }
        finally
        {
            _hashGate.Release();
        }
    }

    /// <summary>Hashes and stores the password for a user (no policy check).</summary>
    public async Task SetPasswordAsync(string userId, string password)
    {
        var (encoded, salt) = await HashWorkAsync(() => PasswordHasher.Hash(password));
        await _credentialStore.SetPasswordAsync(userId, encoded, salt);
    }

    /// <summary>Verifies a password against the stored credentials (constant-time, bounded concurrency).</summary>
    public async Task<bool> VerifyPasswordAsync(string userId, string password)
    {
        var creds = await _credentialStore.GetCredentialsAsync(userId);
        if (creds == null)
        {
            await HashWorkAsync(() => PasswordHasher.Verify(password, DummyHash));
            return false;
        }

        var valid = await HashWorkAsync(() => PasswordHasher.Verify(password, creds.PasswordHash, creds.Salt));
        if (valid && PasswordHasher.NeedsRehash(creds.PasswordHash))
            await SetPasswordAsync(userId, password);
        return valid;
    }

    // -------------------------------------------------------------------------
    // Lockout (atomic: an attempt is reserved before PBKDF2 runs)
    // -------------------------------------------------------------------------

    private static string UserKey(string userId) => "u:" + userId;

    private static string NameKey(string attempted) => "n:" + attempted.Trim().ToLowerInvariant();

    public bool IsLocked(string userId) => IsKeyLocked(UserKey(userId));

    private bool IsKeyLocked(string key) =>
        _lockouts.TryGetValue(key, out var state) && Volatile.Read(ref state.LockedUntil) > Now();

    /// <summary>
    /// Reserves an attempt for <paramref name="key"/>: false when locked. The attempt counts as a
    /// failure until <see cref="AttemptSucceeded"/> resets it, so concurrent requests cannot exceed
    /// <see cref="UsersModuleOptions.MaxLoginAttempts"/>. Failures older than
    /// <see cref="UsersModuleOptions.LockoutMs"/> no longer count. Above
    /// <see cref="MaxTrackedLockouts"/> entries, expired ones are pruned first.
    /// </summary>
    private bool TryReserveAttempt(string key)
    {
        if (_lockouts.Count > MaxTrackedLockouts) PruneLockouts();

        while (true)
        {
            var state = _lockouts.GetOrAdd(key, _ => new LockoutState());
            lock (state)
            {
                // Pruned or reset between the lookup and the lock: use the table's current entry.
                if (state.Removed) continue;

                var now = Now();
                if (state.LockedUntil > now) return false;
                if (state.LockedUntil != 0 || now - state.LastFailure > _options.LockoutMs)
                {
                    state.LockedUntil = 0;
                    state.Failures = 0;
                }
                state.Failures++;
                state.LastFailure = now;
                if (state.Failures >= Math.Max(1, _options.MaxLoginAttempts))
                    state.LockedUntil = now + _options.LockoutMs;
                return true;
            }
        }
    }

    private void AttemptSucceeded(string key) => RemoveLockout(key, _ => true);

    /// <summary>
    /// Removes entries whose failures all expired (not locked, last failure older than the lockout
    /// window). Partial failure counts that still apply are kept.
    /// </summary>
    private void PruneLockouts()
    {
        var now = Now();
        foreach (var key in _lockouts.Keys)
            RemoveLockout(key, state => state.LockedUntil <= now && now - state.LastFailure > _options.LockoutMs);
    }

    /// <summary>
    /// Removes the entry for <paramref name="key"/> when <paramref name="predicate"/> holds, atomically
    /// with respect to <see cref="TryReserveAttempt"/> (an attempt in progress is never lost).
    /// </summary>
    private void RemoveLockout(string key, Func<LockoutState, bool> predicate)
    {
        if (!_lockouts.TryGetValue(key, out var state)) return;
        lock (state)
        {
            if (state.Removed || !predicate(state)) return;
            state.Removed = true;
            _lockouts.TryRemove(new KeyValuePair<string, LockoutState>(key, state));
        }
    }

    /// <summary>Clears lockout state for a user (admin password reset, user deletion).</summary>
    public void ClearLockout(string userId) => RemoveLockout(UserKey(userId), _ => true);

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
    /// Unknown names are locked like known users and spend the same PBKDF2 time.
    /// </summary>
    public async Task<LoginResponse> LoginAsync(string username, string password, SessionMetadata? metadata = null)
    {
        metadata ??= new SessionMetadata();
        var auditMeta = new AuditMetadata { RemoteAddress = metadata.RemoteAddress };

        var user = await FindByNameOrEmailAsync(username);
        var auditName = user?.Name ?? UsersValidation.Sanitize(username, UsersValidation.MaxNameLength);
        var key = user != null ? UserKey(user.Id) : NameKey(username);

        if (!TryReserveAttempt(key))
        {
            await AuditAsync(AuditEvents.LoginFailed, auditName, "account locked", metadata: auditMeta, userId: user?.Id);
            throw new UsersApiException(429, AccountLockedMessage);
        }

        bool valid;
        if (user != null)
        {
            valid = await VerifyPasswordAsync(user.Id, password);
        }
        else
        {
            await HashWorkAsync(() => PasswordHasher.Verify(password, DummyHash));
            valid = false;
        }

        if (!valid || user == null)
        {
            await AuditAsync(AuditEvents.LoginFailed, auditName, user == null ? "unknown user" : "wrong password",
                metadata: auditMeta, userId: user?.Id);
            throw UsersApiException.Unauthorized(InvalidCredentialsMessage);
        }

        AttemptSucceeded(key);

        if (user.Disabled)
        {
            await AuditAsync(AuditEvents.LoginFailed, user.Name, "account disabled", metadata: auditMeta, userId: user.Id);
            throw UsersApiException.Forbidden(AccountDisabledMessage);
        }

        var now = Now();
        user = await _userStore.UpdateUserAsync(user.Id, new UpdateUserDto { LastLoginAt = now });

        var token = GenerateToken();
        var session = await _sessionStore.CreateSessionAsync(new Session
        {
            Id = Guid.NewGuid().ToString(),
            TokenHash = HashToken(token),
            UserId = user.Id,
            UserName = user.Name,
            LoginTime = now,
            LastActivity = now,
            Metadata = metadata,
        });

        await AuditAsync(AuditEvents.Login, user.Name, "login successful",
            metadata: new AuditMetadata { RemoteAddress = metadata.RemoteAddress, SessionId = session.Id },
            userId: user.Id);

        return new LoginResponse
        {
            Token = token,
            Session = new CurrentSession
            {
                Id = session.Id,
                User = user,
                Permissions = await GetEffectivePermissionsAsync(user),
                LoginTime = session.LoginTime,
                LastActivity = session.LastActivity,
                ExpiresAt = ExpiresAt(session.LoginTime, session.LastActivity),
            },
        };
    }

    /// <summary>Ends a session (logout).</summary>
    public async Task LogoutAsync(Session session)
    {
        await _sessionStore.DeleteSessionByIdAsync(session.Id);
        _shells?.CloseBySession(session.Id);
        await AuditAsync(AuditEvents.Logout, session.UserName, "user logged out",
            actor: session.UserName, metadata: new AuditMetadata { SessionId = session.Id },
            userId: session.UserId, actorId: session.UserId);
    }

    /// <summary>Ends the session associated with a raw bearer token.</summary>
    public async Task LogoutAsync(string token)
    {
        var session = await _sessionStore.GetSessionAsync(HashToken(token));
        if (session != null) await LogoutAsync(session);
    }

    /// <summary>
    /// Verifies a user's password through the lockout (429 when locked; failures counted and
    /// audited as <c>login_failed</c>). Used by verify-password and self password change.
    /// </summary>
    public async Task<bool> VerifyPasswordWithLockoutAsync(User user, string password, string? actor, string? actorId = null)
    {
        var key = UserKey(user.Id);
        if (!TryReserveAttempt(key))
            throw new UsersApiException(429, AccountLockedMessage);

        var valid = await VerifyPasswordAsync(user.Id, password);
        if (valid)
        {
            AttemptSucceeded(key);
        }
        else
        {
            await AuditAsync(AuditEvents.LoginFailed, user.Name, "password verification failed", actor,
                userId: user.Id, actorId: actorId);
        }
        return valid;
    }

    // -------------------------------------------------------------------------
    // Request authentication
    // -------------------------------------------------------------------------

    /// <summary>
    /// Authenticates a raw bearer token (looked up by its hash). Throws 401 on failure.
    /// </summary>
    public async Task<UsersAuthContext> AuthenticateTokenAsync(string token)
    {
        var session = await _sessionStore.GetSessionAsync(HashToken(token))
            ?? throw UsersApiException.Unauthorized("invalid or expired token");
        return await AuthenticateSessionAsync(session);
    }

    /// <summary>
    /// Validates a stored session: idle and absolute expiry (expired sessions are deleted), live
    /// user (missing or disabled → 401); slides <c>lastActivity</c>.
    /// </summary>
    public async Task<UsersAuthContext> AuthenticateSessionAsync(Session session)
    {
        var now = Now();
        if (now - session.LastActivity > SessionTimeoutMs || now - session.LoginTime > MaxSessionLifetimeMs)
        {
            await _sessionStore.DeleteSessionByIdAsync(session.Id);
            throw UsersApiException.Unauthorized("session expired");
        }

        var user = await _userStore.GetUserAsync(session.UserId);
        if (user == null || user.Id != session.UserId)
        {
            await _sessionStore.DeleteSessionByIdAsync(session.Id);
            throw UsersApiException.Unauthorized("invalid or expired token");
        }

        if (user.Disabled)
            throw UsersApiException.Unauthorized(AccountDisabledMessage);

        await _sessionStore.UpdateLastActivityAsync(session.TokenHash, now);
        session.LastActivity = now;

        return new UsersAuthContext
        {
            User = user,
            Permissions = await GetEffectivePermissionsAsync(user),
            Session = session,
            SessionId = session.Id,
            LoginTime = session.LoginTime,
            LastActivity = now,
            ExpiresAt = ExpiresAt(session.LoginTime, now),
        };
    }

    /// <summary>
    /// Builds the auth context for a request authenticated by an external adapter.
    /// <paramref name="request"/> is the request the adapter accepted (kept for re-validation).
    /// </summary>
    public async Task<UsersAuthContext> AuthenticateExternalAsync(ExternalAuthResult result,
        ExternalRequestSnapshot? request = null)
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
            ExternalResult = result,
            ExternalRequest = request,
        };
    }

    /// <summary>
    /// Authenticates an HTTP request with the external adapter (when configured) or the
    /// <c>Authorization: Bearer</c> token. Returns null when no credentials were presented;
    /// throws 401 for invalid credentials.
    /// </summary>
    public async Task<UsersAuthContext?> AuthenticateRequestAsync(HttpContext context, IExternalAuthAdapter? adapter)
    {
        if (adapter != null)
        {
            var result = adapter.Authenticate(context);
            return result?.User == null
                ? null
                : await AuthenticateExternalAsync(result, ExternalRequestSnapshot.Capture(context));
        }

        var token = BearerToken(context);
        return token == null ? null : await AuthenticateTokenAsync(token);
    }

    /// <summary>The bearer token from the Authorization header, or null.</summary>
    public static string? BearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        var token = header["Bearer ".Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    // -------------------------------------------------------------------------
    // WebSocket tickets
    // -------------------------------------------------------------------------

    /// <summary>Issues a single-use WebSocket ticket bound to the caller's session (30 s).</summary>
    public WsTicketResponse IssueTicket(UsersAuthContext auth)
    {
        var now = Now();
        if (_tickets.Count > MaxOutstandingTickets)
        {
            foreach (var (k, t) in _tickets)
                if (t.ExpiresAt <= now) _tickets.TryRemove(k, out _);
        }

        var ticket = GenerateToken();
        _tickets[HashToken(ticket)] = new WsTicket(auth.Session?.Id, auth.ExternalResult, auth.ExternalRequest,
            now + TicketLifetimeSeconds * 1000L);
        return new WsTicketResponse { Ticket = ticket, ExpiresIn = TicketLifetimeSeconds };
    }

    /// <summary>
    /// Consumes a ticket (single use) and authenticates its session. Null when the ticket is
    /// unknown, used, expired, or its session is no longer valid.
    /// </summary>
    public async Task<UsersAuthContext?> ConsumeTicketAsync(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return null;
        if (!_tickets.TryRemove(HashToken(ticket), out var entry)) return null;
        if (entry.ExpiresAt <= Now()) return null;

        // External mode: the ticket keeps the request the adapter accepted when it was issued.
        if (entry.External != null)
            return await AuthenticateExternalAsync(entry.External, entry.ExternalRequest);

        if (entry.SessionId == null) return null;
        var session = await _sessionStore.GetSessionByIdAsync(entry.SessionId);
        if (session == null) return null;
        try
        {
            return await AuthenticateSessionAsync(session);
        }
        catch (UsersApiException)
        {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Sessions
    // -------------------------------------------------------------------------

    /// <summary>Listing view of a stored session (never includes the token or its hash).</summary>
    public SessionInfo ToSessionInfo(Session session, string? userName, string? currentSessionId) => new()
    {
        Id = session.Id,
        UserId = session.UserId,
        UserName = userName ?? session.UserName,
        CreatedAt = session.LoginTime,
        LastActivity = session.LastActivity,
        ExpiresAt = ExpiresAt(session.LoginTime, session.LastActivity),
        Metadata = new SessionMetadata
        {
            UserAgent = session.Metadata?.UserAgent,
            RemoteAddress = session.Metadata?.RemoteAddress,
        },
        Current = currentSessionId != null && session.Id == currentSessionId,
    };

    /// <summary>
    /// Revokes a session: deletes it, audits <c>session_revoked</c> and sends <c>session-kicked</c>
    /// to the event sockets of that user only.
    /// </summary>
    public async Task RevokeSessionAsync(Session session, string reason, string? actor, string? actorId = null)
    {
        await _sessionStore.DeleteSessionByIdAsync(session.Id);
        _shells?.CloseBySession(session.Id);
        await AuditAsync(AuditEvents.SessionRevoked, session.UserName, reason, actor,
            new AuditMetadata { SessionId = session.Id }, session.UserId, actorId);
        await SendSessionKickedAsync(session, reason);
    }

    /// <summary>Revokes all sessions of a user, optionally keeping one.</summary>
    public async Task RevokeUserSessionsAsync(string userId, string reason, string? actor,
        string? exceptSessionId = null, string? actorId = null)
    {
        var sessions = await _sessionStore.GetUserSessionsAsync(userId);
        foreach (var session in sessions.Where(s => s.Id != exceptSessionId))
            await RevokeSessionAsync(session, reason, actor, actorId);
        // Also ends shells not tied to a stored session (e.g. external-adapter principals).
        if (exceptSessionId == null) _shells?.CloseByUser(userId);
    }

    private async Task SendSessionKickedAsync(Session session, string reason)
    {
        if (_events == null) return;
        try
        {
            var message = JsonSerializer.Serialize(new
            {
                type = "session-kicked",
                data = new { sessionId = session.Id, userId = session.UserId, reason },
            });
            await _events.SendToUserAsync(session.UserId, message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send session-kicked for session {SessionId}", session.Id);
        }
    }

    /// <summary>
    /// Asks open shells to re-validate their session and <c>shell:execute</c> now (after a user,
    /// group or membership change).
    /// </summary>
    public void NotifyPermissionsChanged() => _shells?.RequestRecheck();

    /// <summary>
    /// Re-validates a stored session for a long-lived connection (an open shell) without the
    /// side effects of a request: null when the session is gone, expired (idle or absolute), or the
    /// user is missing, disabled or must change the password. <paramref name="touch"/> slides
    /// <c>lastActivity</c> (the connection was used since the last check).
    /// </summary>
    public async Task<UsersAuthContext?> RevalidateSessionAsync(string sessionId, bool touch)
    {
        var session = await _sessionStore.GetSessionByIdAsync(sessionId);
        if (session == null) return null;

        var now = Now();
        if (now - session.LastActivity > SessionTimeoutMs || now - session.LoginTime > MaxSessionLifetimeMs)
        {
            await _sessionStore.DeleteSessionByIdAsync(session.Id);
            return null;
        }

        var user = await _userStore.GetUserAsync(session.UserId);
        if (user == null || user.Id != session.UserId || user.Disabled || user.MustChangePassword) return null;

        if (touch)
        {
            await _sessionStore.UpdateLastActivityAsync(session.TokenHash, now);
            session.LastActivity = now;
        }

        return new UsersAuthContext
        {
            User = user,
            Permissions = await GetEffectivePermissionsAsync(user),
            Session = session,
            SessionId = session.Id,
            LoginTime = session.LoginTime,
            LastActivity = session.LastActivity,
            ExpiresAt = ExpiresAt(session.LoginTime, session.LastActivity),
        };
    }

    /// <summary>
    /// Remembers the request behind an external-adapter <paramref name="principal"/> so a long-lived
    /// connection (an open shell) can be re-validated with <see cref="RevalidateExternalAsync"/>.
    /// </summary>
    public void BindExternalRequest(CliPrincipal principal, ExternalRequestSnapshot request) =>
        _externalRequests.AddOrUpdate(principal, request);

    /// <summary>
    /// Re-validates an external-adapter principal by running <paramref name="adapter"/> again on the
    /// request it originally accepted (see <see cref="BindExternalRequest"/>). Null when no request is
    /// known, the adapter returns no user, a different user, or throws.
    /// </summary>
    public async Task<UsersAuthContext?> RevalidateExternalAsync(CliPrincipal principal, IExternalAuthAdapter adapter,
        IServiceProvider? services = null)
    {
        if (!_externalRequests.TryGetValue(principal, out var request)) return null;

        ExternalAuthResult? result;
        try
        {
            result = adapter.Authenticate(await request.ToAuthenticatedHttpContextAsync(services));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "External auth adapter failed while re-validating user {UserId}", principal.UserId);
            return null;
        }
        if (result?.User == null || result.User.Id != principal.UserId) return null;

        return await AuthenticateExternalAsync(result, request);
    }

    /// <summary>Deletes sessions idle for longer than the session timeout.</summary>
    public Task SweepIdleSessionsAsync() =>
        _sessionStore.DeleteIdleSessionsAsync(Now() - SessionTimeoutMs);

    // -------------------------------------------------------------------------
    // Audit
    // -------------------------------------------------------------------------

    /// <summary>Logs an audit entry; username and details are sanitized (no control characters).</summary>
    public Task AuditAsync(string @event, string username, string? details = null, string? actor = null,
        AuditMetadata? metadata = null, string? userId = null, string? actorId = null) =>
        _auditStore.LogAsync(new AuditEntry
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = Now(),
            Event = @event,
            Username = UsersValidation.Sanitize(username, 256),
            UserId = userId,
            Details = details == null ? null : UsersValidation.Sanitize(details),
            Actor = actor == null ? null : UsersValidation.Sanitize(actor, 256),
            ActorId = actorId,
            Metadata = metadata,
        });
}
