using System.Security.Cryptography;
using System.Text;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// Handles password hashing, token generation, login, and logout with audit logging.
/// </summary>
public class AuthService
{
    private readonly IUserStore _userStore;
    private readonly ISessionStore _sessionStore;
    private readonly ICredentialStore _credentialStore;
    private readonly IAuditStore _auditStore;

    public AuthService(
        IUserStore userStore,
        ISessionStore sessionStore,
        ICredentialStore credentialStore,
        IAuditStore auditStore)
    {
        _userStore = userStore;
        _sessionStore = sessionStore;
        _credentialStore = credentialStore;
        _auditStore = auditStore;
    }

    /// <summary>
    /// Generates a cryptographically secure opaque bearer token.
    /// </summary>
    public string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string HashPassword(string password, string salt) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(salt + password))
        ).ToLowerInvariant();

    /// <summary>
    /// Sets the password for a user, generating a new random salt.
    /// </summary>
    public async Task SetPasswordAsync(string userId, string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var salt = Convert.ToHexString(saltBytes).ToLowerInvariant();
        var hash = HashPassword(password, salt);
        await _credentialStore.SetPasswordAsync(userId, hash, salt);
    }

    /// <summary>
    /// Verifies a password against the stored credentials for a user.
    /// </summary>
    public async Task<bool> VerifyPasswordAsync(string userId, string password)
    {
        var creds = await _credentialStore.GetCredentialsAsync(userId);
        if (creds == null) return false;
        var hash = HashPassword(password, creds.Salt);
        return hash == creds.PasswordHash;
    }

    /// <summary>
    /// Authenticates a user and creates a session.
    /// </summary>
    public async Task<LoginResponse> LoginAsync(string username, string password, SessionMetadata? metadata = null)
    {
        metadata ??= new SessionMetadata();

        var user = await _userStore.GetUserAsync(username);
        if (user == null)
        {
            await _auditStore.LogAsync(new AuditEntry
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Event = "login_failed",
                Username = username,
                Details = "User not found",
                Metadata = new AuditMetadata { RemoteAddress = metadata.RemoteAddress },
            });
            throw new InvalidOperationException("Invalid credentials");
        }

        if (user.Disabled == true)
        {
            await _auditStore.LogAsync(new AuditEntry
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Event = "login_failed",
                Username = username,
                Details = "Account disabled",
                Metadata = new AuditMetadata { RemoteAddress = metadata.RemoteAddress },
            });
            throw new InvalidOperationException("Account is disabled");
        }

        var valid = await VerifyPasswordAsync(user.Id, password);
        if (!valid)
        {
            await _auditStore.LogAsync(new AuditEntry
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Event = "login_failed",
                Username = username,
                Details = "Wrong password",
                Metadata = new AuditMetadata { RemoteAddress = metadata.RemoteAddress },
            });
            throw new InvalidOperationException("Invalid credentials");
        }

        user.LastLoginAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var token = GenerateToken();
        var session = await _sessionStore.CreateSessionAsync(user.Id, token, user, metadata);

        await _auditStore.LogAsync(new AuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Event = "login",
            Username = username,
            Details = "Login successful",
            Metadata = new AuditMetadata
            {
                RemoteAddress = metadata.RemoteAddress,
                SessionId = session.Id,
            },
        });

        return new LoginResponse { Token = token, Session = session };
    }

    /// <summary>
    /// Logs out the session associated with the given token.
    /// </summary>
    public async Task LogoutAsync(string token)
    {
        var session = await _sessionStore.GetSessionAsync(token);
        var username = session?.User?.Name ?? "unknown";

        await _sessionStore.DeleteSessionAsync(token);

        await _auditStore.LogAsync(new AuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Event = "logout",
            Username = username,
            Details = "User logged out",
            Metadata = new AuditMetadata { SessionId = session?.Id },
        });
    }
}
