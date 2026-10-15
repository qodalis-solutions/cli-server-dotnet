using System.Collections.Concurrent;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Stores;

/// <summary>
/// In-memory implementation of <see cref="IApiTokenStore"/>.
/// </summary>
public class InMemoryApiTokenStore : IApiTokenStore
{
    private readonly ConcurrentDictionary<string, ApiToken> _tokens = new();

    public Task<ApiToken> CreateTokenAsync(ApiToken token)
    {
        _tokens[token.Id] = token.Clone();
        return Task.FromResult(token.Clone());
    }

    public Task<ApiToken?> GetTokenByHashAsync(string tokenHash) =>
        Task.FromResult(_tokens.Values.FirstOrDefault(t => t.TokenHash == tokenHash)?.Clone());

    public Task<ApiToken?> GetTokenAsync(string id) =>
        Task.FromResult(_tokens.TryGetValue(id, out var token) ? token.Clone() : null);

    public Task<List<ApiToken>> GetTokensAsync(string? userId = null) =>
        Task.FromResult(_tokens.Values
            .Where(t => userId == null || t.UserId == userId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.Clone())
            .ToList());

    public Task DeleteTokenAsync(string id)
    {
        _tokens.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task DeleteUserTokensAsync(string userId)
    {
        foreach (var kvp in _tokens.Where(kvp => kvp.Value.UserId == userId).ToList())
            _tokens.TryRemove(kvp.Key, out _);
        return Task.CompletedTask;
    }

    public Task UpdateLastUsedAsync(string id, long lastUsedAt)
    {
        if (_tokens.TryGetValue(id, out var token))
            token.LastUsedAt = lastUsedAt;
        return Task.CompletedTask;
    }
}
