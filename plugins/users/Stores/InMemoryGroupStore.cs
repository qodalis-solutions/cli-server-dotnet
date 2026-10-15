using System.Collections.Concurrent;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Stores;

/// <summary>
/// In-memory implementation of <see cref="IGroupStore"/>. Names are case-insensitive keys.
/// </summary>
public class InMemoryGroupStore : IGroupStore
{
    private readonly ConcurrentDictionary<string, Group> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public Task<List<Group>> GetGroupsAsync() =>
        Task.FromResult(_groups.Values
            .OrderBy(g => g.CreatedAt)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Clone())
            .ToList());

    public Task<Group?> GetGroupAsync(string name) =>
        Task.FromResult(_groups.TryGetValue(name, out var group) ? group.Clone() : null);

    public Task<Group> CreateGroupAsync(Group group)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stored = group.Clone();
        if (string.IsNullOrEmpty(stored.Id)) stored.Id = Guid.NewGuid().ToString();
        if (stored.CreatedAt == 0) stored.CreatedAt = now;
        if (stored.UpdatedAt == 0) stored.UpdatedAt = stored.CreatedAt;

        if (!_groups.TryAdd(stored.Name, stored))
            throw new InvalidOperationException($"Group '{group.Name}' already exists");

        return Task.FromResult(stored.Clone());
    }

    public Task<Group> UpdateGroupAsync(string name, UpdateGroupDto data)
    {
        lock (_lock)
        {
            if (!_groups.TryGetValue(name, out var group))
                throw new InvalidOperationException("Group not found");

            if (data.Description != null) group.Description = data.Description;
            if (data.Permissions != null) group.Permissions = [.. data.Permissions];
            group.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return Task.FromResult(group.Clone());
        }
    }

    public Task DeleteGroupAsync(string name)
    {
        _groups.TryRemove(name, out _);
        return Task.CompletedTask;
    }
}
