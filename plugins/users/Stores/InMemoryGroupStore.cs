using System.Collections.Concurrent;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Stores;

/// <summary>
/// In-memory implementation of <see cref="IGroupStore"/>.
/// </summary>
public class InMemoryGroupStore : IGroupStore
{
    private readonly ConcurrentDictionary<string, Group> _groups = new(StringComparer.OrdinalIgnoreCase);

    public Task<List<Group>> GetGroupsAsync() =>
        Task.FromResult(_groups.Values.ToList());

    public Task<Group?> GetGroupAsync(string name) =>
        Task.FromResult(_groups.TryGetValue(name, out var group) ? group : null);

    public Task<Group> CreateGroupAsync(string name, string? description = null)
    {
        var group = new Group
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Description = description,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        if (!_groups.TryAdd(name, group))
            throw new InvalidOperationException($"Group '{name}' already exists");

        return Task.FromResult(group);
    }

    public Task DeleteGroupAsync(string name)
    {
        _groups.TryRemove(name, out _);
        return Task.CompletedTask;
    }
}
