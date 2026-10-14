using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// User and group management with the contract's validations, protections, cascades
/// and audit events. Mutations are serialized so that protection checks (last admin,
/// duplicates) are not raced. Errors are thrown as <see cref="UsersApiException"/>.
/// </summary>
public class UsersService
{
    private readonly IUserStore _userStore;
    private readonly IGroupStore _groupStore;
    private readonly ICredentialStore _credentialStore;
    private readonly IAuditStore _auditStore;
    private readonly AuthService _auth;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UsersService(
        IUserStore userStore,
        IGroupStore groupStore,
        ICredentialStore credentialStore,
        IAuditStore auditStore,
        AuthService authService)
    {
        _userStore = userStore;
        _groupStore = groupStore;
        _credentialStore = credentialStore;
        _auditStore = auditStore;
        _auth = authService;
    }

    // =========================================================================
    // Users
    // =========================================================================

    /// <summary>Lists users; <paramref name="query"/> matches name/email substrings (case-insensitive).</summary>
    public async Task<List<User>> ListUsersAsync(string? query, int? skip, int? take)
    {
        if (skip < 0) throw UsersApiException.BadRequest("skip must be >= 0");
        if (take < 0) throw UsersApiException.BadRequest("take must be >= 0");

        IEnumerable<User> users = await _userStore.GetUsersAsync();
        if (!string.IsNullOrEmpty(query))
        {
            users = users.Where(u =>
                u.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                u.Email.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        if (skip is > 0) users = users.Skip(skip.Value);
        if (take.HasValue) users = users.Take(take.Value);
        return users.ToList();
    }

    public Task<User?> FindUserAsync(string idOrName) => _userStore.GetUserAsync(idOrName);

    public async Task<User> GetUserOrThrowAsync(string idOrName) =>
        await _userStore.GetUserAsync(idOrName) ?? throw UsersApiException.NotFound("user not found");

    public async Task<UserPermissionsResponse> GetUserPermissionsAsync(User user) => new()
    {
        UserId = user.Id,
        Groups = [.. user.Groups],
        Permissions = await _auth.GetEffectivePermissionsAsync(user),
    };

    public async Task<User> CreateUserAsync(CreateUserDto dto, string? actor)
    {
        var name = dto.Name?.Trim();
        var email = dto.Email?.Trim();
        if (string.IsNullOrEmpty(name)) throw UsersApiException.BadRequest("name is required");
        if (string.IsNullOrEmpty(email)) throw UsersApiException.BadRequest("email is required");
        if (dto.Password != null) _auth.ValidatePassword(dto.Password);

        await _gate.WaitAsync();
        try
        {
            await EnsureUniqueAsync(name, email, exceptUserId: null);
            var groups = dto.Groups is { Count: > 0 }
                ? await ResolveGroupNamesAsync(dto.Groups)
                : [Group.UsersGroup];

            User user;
            try
            {
                user = await _userStore.CreateUserAsync(new CreateUserDto
                {
                    Name = name,
                    Email = email,
                    Groups = groups,
                    Disabled = dto.Disabled ?? false,
                    HomeDir = dto.HomeDir,
                    Data = dto.Data,
                });
            }
            catch (InvalidOperationException ex)
            {
                throw UsersApiException.Conflict(ex.Message);
            }

            if (!string.IsNullOrEmpty(dto.Password))
                await _auth.SetPasswordAsync(user.Id, dto.Password);

            await _auth.AuditAsync(AuditEvents.UserCreated, user.Name, null, actor);
            return user;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Updates a user. <paramref name="caller"/> without <c>users:update</c> may only change
    /// their own <c>email</c> and <c>data</c>.
    /// </summary>
    public async Task<User> UpdateUserAsync(string idOrName, UpdateUserDto dto, UsersAuthContext caller)
    {
        await _gate.WaitAsync();
        try
        {
            var target = await _userStore.GetUserAsync(idOrName);
            var privileged = caller.Can(UsersPermissions.UsersUpdate);

            if (target == null)
            {
                throw privileged
                    ? UsersApiException.NotFound("user not found")
                    : UsersApiException.Forbidden($"permission denied: {UsersPermissions.UsersUpdate}");
            }

            if (!privileged)
            {
                if (!caller.IsSelf(target))
                    throw UsersApiException.Forbidden($"permission denied: {UsersPermissions.UsersUpdate}");

                var changesRestricted =
                    (dto.Name != null && dto.Name != target.Name) ||
                    (dto.Groups != null && !SameGroups(dto.Groups, target.Groups)) ||
                    (dto.Disabled.HasValue && dto.Disabled.Value != target.Disabled) ||
                    (dto.HomeDir != null && dto.HomeDir != target.HomeDir);
                if (changesRestricted)
                    throw UsersApiException.Forbidden("you may only change your own email and data");
            }

            var update = new UpdateUserDto { Data = dto.Data };

            if (dto.Name != null)
            {
                var name = dto.Name.Trim();
                if (name.Length == 0) throw UsersApiException.BadRequest("name must not be empty");
                if (name != target.Name) update.Name = name;
            }
            if (dto.Email != null)
            {
                var email = dto.Email.Trim();
                if (email.Length == 0) throw UsersApiException.BadRequest("email must not be empty");
                if (email != target.Email) update.Email = email;
            }
            await EnsureUniqueAsync(update.Name, update.Email, target.Id);

            if (dto.Groups != null)
                update.Groups = dto.Groups.Count == 0 ? [] : await ResolveGroupNamesAsync(dto.Groups);
            if (dto.HomeDir != null) update.HomeDir = dto.HomeDir;
            if (dto.Disabled.HasValue && dto.Disabled.Value != target.Disabled)
                update.Disabled = dto.Disabled.Value;

            var losesAdmin = target.IsInGroup(Group.AdminGroup) && !target.Disabled &&
                ((update.Disabled == true) ||
                 (update.Groups != null && !update.Groups.Any(g => string.Equals(g, Group.AdminGroup, StringComparison.OrdinalIgnoreCase))));
            if (losesAdmin && await IsLastEnabledAdminAsync(target))
            {
                throw UsersApiException.Conflict(update.Disabled == true
                    ? "cannot disable the last admin"
                    : "cannot remove the last admin from the admin group");
            }

            User updated;
            try
            {
                updated = await _userStore.UpdateUserAsync(target.Id, update);
            }
            catch (InvalidOperationException ex)
            {
                throw UsersApiException.NotFound(ex.Message);
            }

            var actor = caller.User.Name;
            await _auth.AuditAsync(AuditEvents.UserUpdated, updated.Name, null, actor);

            if (update.Disabled == true)
            {
                await _auth.AuditAsync(AuditEvents.UserDisabled, updated.Name, null, actor);
                await _auth.RevokeUserSessionsAsync(updated.Id, AuthService.RevokeReasons.UserDisabled, actor);
            }
            else if (update.Disabled == false)
            {
                await _auth.AuditAsync(AuditEvents.UserEnabled, updated.Name, null, actor);
            }

            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteUserAsync(string idOrName, UsersAuthContext caller)
    {
        await _gate.WaitAsync();
        try
        {
            var target = await GetUserOrThrowAsync(idOrName);
            if (caller.IsSelf(target))
                throw UsersApiException.Conflict("cannot delete yourself");
            if (target.IsInGroup(Group.AdminGroup) && !target.Disabled && await IsLastEnabledAdminAsync(target))
                throw UsersApiException.Conflict("cannot delete the last admin");

            await _auth.RevokeUserSessionsAsync(target.Id, AuthService.RevokeReasons.UserDeleted, caller.User.Name);
            await _userStore.DeleteUserAsync(target.Id);
            await _credentialStore.DeleteCredentialsAsync(target.Id);
            _auth.ClearLockout(target.Id);

            await _auth.AuditAsync(AuditEvents.UserDeleted, target.Name, null, caller.User.Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Changes a password. Self requires the correct current password (wrong → 403) and revokes
    /// the caller's other sessions; otherwise <c>users:update</c> is required and all of the
    /// target's sessions are revoked.
    /// </summary>
    public async Task ChangePasswordAsync(string idOrName, PasswordChangeRequest request, UsersAuthContext caller)
    {
        var target = await _userStore.GetUserAsync(idOrName);
        var privileged = caller.Can(UsersPermissions.UsersUpdate);

        if (target == null)
        {
            throw privileged
                ? UsersApiException.NotFound("user not found")
                : UsersApiException.Forbidden($"permission denied: {UsersPermissions.UsersUpdate}");
        }

        var isSelf = caller.IsSelf(target);
        if (!isSelf && !privileged)
            throw UsersApiException.Forbidden($"permission denied: {UsersPermissions.UsersUpdate}");

        _auth.ValidatePassword(request.Password);

        if (isSelf)
        {
            if (string.IsNullOrEmpty(request.CurrentPassword))
                throw UsersApiException.BadRequest("currentPassword is required");
            if (!await _auth.VerifyPasswordAsync(target.Id, request.CurrentPassword))
                throw UsersApiException.Forbidden("current password is incorrect");
        }

        await _auth.SetPasswordAsync(target.Id, request.Password!);
        await _auth.AuditAsync(AuditEvents.PasswordChanged, target.Name, isSelf ? "changed by self" : null, caller.User.Name);

        await _auth.RevokeUserSessionsAsync(target.Id, AuthService.RevokeReasons.PasswordChanged, caller.User.Name,
            exceptSessionId: isSelf ? caller.Session?.Id : null);
    }

    // =========================================================================
    // Groups
    // =========================================================================

    public Task<List<Group>> ListGroupsAsync() => _groupStore.GetGroupsAsync();

    public async Task<Group> GetGroupOrThrowAsync(string name) =>
        await _groupStore.GetGroupAsync(name) ?? throw UsersApiException.NotFound("group not found");

    public async Task<List<User>> GetGroupMembersAsync(Group group)
    {
        var users = await _userStore.GetUsersAsync();
        return users.Where(u => u.IsInGroup(group.Name)).ToList();
    }

    public async Task<Group> CreateGroupAsync(CreateGroupRequest request, string? actor)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name)) throw UsersApiException.BadRequest("name is required");
        var permissions = ValidatePermissions(request.Permissions) ?? [];

        await _gate.WaitAsync();
        try
        {
            if (await _groupStore.GetGroupAsync(name) != null)
                throw UsersApiException.Conflict($"group '{name}' already exists");

            Group group;
            try
            {
                group = await _groupStore.CreateGroupAsync(new Group
                {
                    Name = name,
                    Description = request.Description,
                    Permissions = permissions,
                    BuiltIn = false,
                });
            }
            catch (InvalidOperationException ex)
            {
                throw UsersApiException.Conflict(ex.Message);
            }

            await _auth.AuditAsync(AuditEvents.GroupCreated, actor ?? string.Empty, $"group '{group.Name}' created", actor);
            return group;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Group> UpdateGroupAsync(string name, UpdateGroupDto dto, string? actor)
    {
        var permissions = ValidatePermissions(dto.Permissions);

        await _gate.WaitAsync();
        try
        {
            var group = await GetGroupOrThrowAsync(name);

            if (permissions != null &&
                string.Equals(group.Name, Group.AdminGroup, StringComparison.OrdinalIgnoreCase) &&
                !SameGroups(permissions, group.Permissions))
            {
                throw UsersApiException.Conflict("the admin group's permissions cannot be changed");
            }

            Group updated;
            try
            {
                updated = await _groupStore.UpdateGroupAsync(group.Name,
                    new UpdateGroupDto { Description = dto.Description, Permissions = permissions });
            }
            catch (InvalidOperationException ex)
            {
                throw UsersApiException.NotFound(ex.Message);
            }

            await _auth.AuditAsync(AuditEvents.GroupUpdated, actor ?? string.Empty, $"group '{updated.Name}' updated", actor);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteGroupAsync(string name, string? actor)
    {
        await _gate.WaitAsync();
        try
        {
            var group = await GetGroupOrThrowAsync(name);
            if (group.IsBuiltIn)
                throw UsersApiException.Conflict($"built-in group '{group.Name}' cannot be deleted");

            foreach (var user in await GetGroupMembersAsync(group))
            {
                await _userStore.UpdateUserAsync(user.Id, new UpdateUserDto
                {
                    Groups = user.Groups.Where(g => !string.Equals(g, group.Name, StringComparison.OrdinalIgnoreCase)).ToList(),
                });
            }

            await _groupStore.DeleteGroupAsync(group.Name);
            await _auth.AuditAsync(AuditEvents.GroupDeleted, actor ?? string.Empty, $"group '{group.Name}' deleted", actor);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddMemberAsync(string groupName, string idOrName, string? actor)
    {
        await _gate.WaitAsync();
        try
        {
            var group = await _groupStore.GetGroupAsync(groupName)
                ?? throw UsersApiException.BadRequest($"unknown group: {groupName}");
            var user = await GetUserOrThrowAsync(idOrName);

            if (user.IsInGroup(group.Name)) return;

            await _userStore.UpdateUserAsync(user.Id, new UpdateUserDto { Groups = [.. user.Groups, group.Name] });
            await _auth.AuditAsync(AuditEvents.GroupMemberAdded, user.Name, $"added to group '{group.Name}'", actor);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveMemberAsync(string groupName, string idOrName, string? actor)
    {
        await _gate.WaitAsync();
        try
        {
            var group = await GetGroupOrThrowAsync(groupName);
            var user = await GetUserOrThrowAsync(idOrName);

            if (!user.IsInGroup(group.Name)) return;

            if (string.Equals(group.Name, Group.AdminGroup, StringComparison.OrdinalIgnoreCase) &&
                !user.Disabled && await IsLastEnabledAdminAsync(user))
            {
                throw UsersApiException.Conflict("cannot remove the last admin from the admin group");
            }

            await _userStore.UpdateUserAsync(user.Id, new UpdateUserDto
            {
                Groups = user.Groups.Where(g => !string.Equals(g, group.Name, StringComparison.OrdinalIgnoreCase)).ToList(),
            });
            await _auth.AuditAsync(AuditEvents.GroupMemberRemoved, user.Name, $"removed from group '{group.Name}'", actor);
        }
        finally
        {
            _gate.Release();
        }
    }

    // =========================================================================
    // Audit
    // =========================================================================

    public async Task<List<AuditEntry>> GetAuditAsync(string? @event, string? username, int? limit)
    {
        if (limit < 0) throw UsersApiException.BadRequest("limit must be >= 0");
        return await _auditStore.GetEntriesAsync(new AuditQueryOptions { Event = @event, Username = username, Limit = limit });
    }

    public async Task ClearAuditAsync(string? actor)
    {
        await _auditStore.ClearAsync();
        await _auth.AuditAsync(AuditEvents.AuditCleared, actor ?? string.Empty, "audit log cleared", actor);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private async Task<bool> IsLastEnabledAdminAsync(User user)
    {
        var users = await _userStore.GetUsersAsync();
        return !users.Any(u => u.Id != user.Id && !u.Disabled && u.IsInGroup(Group.AdminGroup));
    }

    private async Task EnsureUniqueAsync(string? name, string? email, string? exceptUserId)
    {
        if (name == null && email == null) return;
        var users = await _userStore.GetUsersAsync();
        foreach (var u in users.Where(u => u.Id != exceptUserId))
        {
            if (name != null && string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase))
                throw UsersApiException.Conflict($"user '{name}' already exists");
            if (email != null && string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase))
                throw UsersApiException.Conflict($"email '{email}' is already in use");
        }
    }

    /// <summary>
    /// Validates that every referenced group exists (400 <c>unknown group: x</c>) and returns
    /// the groups' stored names, de-duplicated case-insensitively.
    /// </summary>
    private async Task<List<string>> ResolveGroupNamesAsync(IEnumerable<string> names)
    {
        var result = new List<string>();
        foreach (var raw in names)
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name)) throw UsersApiException.BadRequest("group names must not be empty");
            var group = await _groupStore.GetGroupAsync(name)
                ?? throw UsersApiException.BadRequest($"unknown group: {name}");
            if (!result.Any(g => string.Equals(g, group.Name, StringComparison.OrdinalIgnoreCase)))
                result.Add(group.Name);
        }
        return result;
    }

    private static List<string>? ValidatePermissions(List<string>? permissions)
    {
        if (permissions == null) return null;
        if (permissions.Any(string.IsNullOrWhiteSpace))
            throw UsersApiException.BadRequest("permissions must be non-empty strings");
        return PermissionMatcher.Merge([permissions]);
    }

    private static bool SameGroups(IEnumerable<string> a, IEnumerable<string> b) =>
        new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b);
}
