using System.Collections.Concurrent;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Stores;

/// <summary>
/// In-memory implementation of <see cref="ISessionStore"/>, keyed by token hash.
/// </summary>
public class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public Task<Session> CreateSessionAsync(Session session)
    {
        _sessions[session.TokenHash] = session.Clone();
        return Task.FromResult(session.Clone());
    }

    public Task<Session?> GetSessionAsync(string tokenHash) =>
        Task.FromResult(_sessions.TryGetValue(tokenHash, out var session) ? session.Clone() : null);

    public Task<Session?> GetSessionByIdAsync(string id) =>
        Task.FromResult(_sessions.Values.FirstOrDefault(s => s.Id == id)?.Clone());

    public Task<List<Session>> GetActiveSessionsAsync() =>
        Task.FromResult(_sessions.Values.OrderBy(s => s.LoginTime).Select(s => s.Clone()).ToList());

    public Task<List<Session>> GetUserSessionsAsync(string userId) =>
        Task.FromResult(_sessions.Values.Where(s => s.UserId == userId)
            .OrderBy(s => s.LoginTime).Select(s => s.Clone()).ToList());

    public Task DeleteSessionAsync(string tokenHash)
    {
        _sessions.TryRemove(tokenHash, out _);
        return Task.CompletedTask;
    }

    public Task DeleteSessionByIdAsync(string id)
    {
        foreach (var kvp in _sessions.Where(kvp => kvp.Value.Id == id).ToList())
            _sessions.TryRemove(kvp.Key, out _);
        return Task.CompletedTask;
    }

    public Task DeleteUserSessionsAsync(string userId)
    {
        foreach (var kvp in _sessions.Where(kvp => kvp.Value.UserId == userId).ToList())
            _sessions.TryRemove(kvp.Key, out _);
        return Task.CompletedTask;
    }

    public Task UpdateLastActivityAsync(string tokenHash, long timestamp)
    {
        if (_sessions.TryGetValue(tokenHash, out var session))
            session.LastActivity = timestamp;
        return Task.CompletedTask;
    }

    public Task DeleteIdleSessionsAsync(long lastActivityBefore)
    {
        foreach (var kvp in _sessions.Where(kvp => kvp.Value.LastActivity < lastActivityBefore).ToList())
            _sessions.TryRemove(kvp.Key, out _);
        return Task.CompletedTask;
    }
}
