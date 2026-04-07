using System.Collections.Concurrent;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Stores;

/// <summary>
/// In-memory implementation of <see cref="ISessionStore"/>.
/// </summary>
public class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public Task<Session> CreateSessionAsync(string userId, string token, User user, SessionMetadata metadata)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var session = new Session
        {
            Id = Guid.NewGuid().ToString(),
            Token = token,
            UserId = userId,
            User = user,
            LoginTime = now,
            LastActivity = now,
            Metadata = metadata,
        };

        _sessions[token] = session;
        return Task.FromResult(session);
    }

    public Task<Session?> GetSessionAsync(string token) =>
        Task.FromResult(_sessions.TryGetValue(token, out var session) ? session : null);

    public Task<Session?> GetSessionByIdAsync(string id) =>
        Task.FromResult(_sessions.Values.FirstOrDefault(s => s.Id == id));

    public Task<List<Session>> GetActiveSessionsAsync() =>
        Task.FromResult(_sessions.Values.ToList());

    public Task DeleteSessionAsync(string token)
    {
        _sessions.TryRemove(token, out _);
        return Task.CompletedTask;
    }

    public Task DeleteSessionByIdAsync(string id)
    {
        var entry = _sessions.FirstOrDefault(kvp => kvp.Value.Id == id);
        if (entry.Key != null)
            _sessions.TryRemove(entry.Key, out _);
        return Task.CompletedTask;
    }

    public Task DeleteUserSessionsAsync(string userId)
    {
        var tokens = _sessions.Where(kvp => kvp.Value.UserId == userId)
            .Select(kvp => kvp.Key).ToList();
        foreach (var token in tokens)
            _sessions.TryRemove(token, out _);
        return Task.CompletedTask;
    }

    public Task UpdateLastActivityAsync(string token)
    {
        if (_sessions.TryGetValue(token, out var session))
            session.LastActivity = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return Task.CompletedTask;
    }

    public Task CleanupAsync(long maxAgeMs)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - maxAgeMs;
        var expired = _sessions.Where(kvp => kvp.Value.LastActivity < cutoff)
            .Select(kvp => kvp.Key).ToList();
        foreach (var token in expired)
            _sessions.TryRemove(token, out _);
        return Task.CompletedTask;
    }
}
