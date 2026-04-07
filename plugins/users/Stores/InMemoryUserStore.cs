using System.Collections.Concurrent;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Stores;

/// <summary>
/// In-memory implementation of <see cref="IUserStore"/>.
/// </summary>
public class InMemoryUserStore : IUserStore
{
    private readonly ConcurrentDictionary<string, User> _users = new();

    public Task<List<User>> GetUsersAsync() =>
        Task.FromResult(_users.Values.ToList());

    public Task<User?> GetUserAsync(string idOrName)
    {
        if (_users.TryGetValue(idOrName, out var user))
            return Task.FromResult<User?>(user);

        var byName = _users.Values.FirstOrDefault(u =>
            string.Equals(u.Name, idOrName, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(byName);
    }

    public Task<User> CreateUserAsync(CreateUserDto data)
    {
        var existing = _users.Values.FirstOrDefault(u =>
            string.Equals(u.Name, data.Name, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
            throw new InvalidOperationException($"User '{data.Name}' already exists");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            Name = data.Name,
            Email = data.Email,
            Groups = data.Groups ?? [],
            Data = data.Data,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _users[user.Id] = user;
        return Task.FromResult(user);
    }

    public Task<User> UpdateUserAsync(string id, UpdateUserDto data)
    {
        if (!_users.TryGetValue(id, out var user))
            throw new InvalidOperationException("User not found");

        if (data.Name != null) user.Name = data.Name;
        if (data.Email != null) user.Email = data.Email;
        if (data.Groups != null) user.Groups = data.Groups;
        if (data.Disabled.HasValue) user.Disabled = data.Disabled;
        if (data.Data != null) user.Data = data.Data;
        user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return Task.FromResult(user);
    }

    public Task DeleteUserAsync(string id)
    {
        _users.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}
