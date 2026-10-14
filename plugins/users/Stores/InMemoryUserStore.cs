using System.Collections.Concurrent;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Stores;

/// <summary>
/// In-memory implementation of <see cref="IUserStore"/>. Returns copies, so callers
/// must persist changes through <see cref="UpdateUserAsync"/>.
/// </summary>
public class InMemoryUserStore : IUserStore
{
    private readonly ConcurrentDictionary<string, User> _users = new();
    private readonly object _lock = new();

    public Task<List<User>> GetUsersAsync() =>
        Task.FromResult(_users.Values
            .OrderBy(u => u.CreatedAt)
            .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .Select(u => u.Clone())
            .ToList());

    public Task<User?> GetUserAsync(string idOrName) =>
        Task.FromResult(Find(idOrName)?.Clone());

    private User? Find(string idOrName)
    {
        if (_users.TryGetValue(idOrName, out var user))
            return user;

        return _users.Values.FirstOrDefault(u =>
            string.Equals(u.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    public Task<User> CreateUserAsync(CreateUserDto data)
    {
        lock (_lock)
        {
            if (_users.Values.Any(u => string.Equals(u.Name, data.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"User '{data.Name}' already exists");

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var user = new User
            {
                Id = Guid.NewGuid().ToString(),
                Name = data.Name,
                Email = data.Email,
                Groups = data.Groups != null ? [.. data.Groups] : [],
                Disabled = data.Disabled ?? false,
                HomeDir = data.HomeDir,
                Data = data.Data,
                CreatedAt = now,
                UpdatedAt = now,
            };

            _users[user.Id] = user;
            return Task.FromResult(user.Clone());
        }
    }

    public Task<User> UpdateUserAsync(string id, UpdateUserDto data)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(id, out var user))
                throw new InvalidOperationException("User not found");

            if (data.Name != null) user.Name = data.Name;
            if (data.Email != null) user.Email = data.Email;
            if (data.Groups != null) user.Groups = [.. data.Groups];
            if (data.Disabled.HasValue) user.Disabled = data.Disabled.Value;
            if (data.HomeDir != null) user.HomeDir = data.HomeDir;
            if (data.Data != null) user.Data = data.Data;
            if (data.LastLoginAt.HasValue) user.LastLoginAt = data.LastLoginAt;
            user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            return Task.FromResult(user.Clone());
        }
    }

    public Task DeleteUserAsync(string id)
    {
        _users.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}
