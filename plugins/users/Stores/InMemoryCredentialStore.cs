using System.Collections.Concurrent;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Stores;

/// <summary>
/// In-memory implementation of <see cref="ICredentialStore"/>.
/// </summary>
public class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, Credentials> _credentials = new();

    public Task SetPasswordAsync(string userId, string hash, string salt)
    {
        _credentials[userId] = new Credentials
        {
            UserId = userId,
            PasswordHash = hash,
            Salt = salt,
            LastChanged = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        return Task.CompletedTask;
    }

    public Task<Credentials?> GetCredentialsAsync(string userId) =>
        Task.FromResult(_credentials.TryGetValue(userId, out var creds) ? creds : null);

    public Task DeleteCredentialsAsync(string userId)
    {
        _credentials.TryRemove(userId, out _);
        return Task.CompletedTask;
    }
}
