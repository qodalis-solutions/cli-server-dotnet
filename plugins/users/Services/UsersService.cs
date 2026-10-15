using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// User and group management with the contract's validations, protections, cascades,
/// anti-escalation rules and audit events. Mutations are serialized so that protection checks
/// (last admin, duplicates) are not raced. Errors are thrown as <see cref="UsersApiException"/>.
/// </summary>
public class UsersService
{
    public const string InsufficientPrivilegesMessage = "insufficient privileges for this user";

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
    // Anti-escalation (B3)
    // =========================================================================

    /// <summary>403 unless the caller holds every permission in <paramref name="permissions"/>.</summary>
    public static void EnsureCanGrant(UsersAuthContext caller, IEnumerable<string> permissions)
    {
        var missing = PermissionMatcher.Missing(caller.Permissions, permissions);
        if (missing.Count > 0)
            throw UsersApiException.Forbidden($"cannot grant permissions you do not hold: {string.Join(", ", missing)}");
    }

    /// <summary>403 unless the caller holds every permission of every named group.</summary>
    private async Task EnsureCanAssignGroupsAsync(UsersAuthContext caller, IEnumerable<string> groupNames)
    {
        var permissions = new List<string>();
        foreach (var name in groupNames)
        {
            var group = await _groupStore.GetGroupAsync(name);
            if (group != null) permissions.AddRange(group.Permissions);
        }
        EnsureCanGrant(caller, permissions);
    }

    /// <summary>403 unless the caller's permissions cover all of the target user's permissions.</summary>
    public async Task EnsureCoversTargetAsync(UsersAuthContext caller, User target)
    {
        if (caller.IsSelf(target)) return;
        var targetPermissions = await _auth.GetEffectivePermissionsAsync(target);
        if (!PermissionMatcher.Covers(caller.Permissions, targetPermissions))
            throw UsersApiException.Forbidden(InsufficientPrivilegesMessage);
    }

    /// <summary><see cref="EnsureCoversTargetAsync(UsersAuthContext, User)"/> by user id (missing users pass).</summary>
    public async Task EnsureCoversTargetAsync(UsersAuthContext caller, string userId)
    {
        var target = await _userStore.GetUserAsync(userId);
        if (target != null && target.Id == userId)
            await EnsureCoversTargetAsync(caller, target);
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

    public async Task<User> CreateUserAsync(CreateUserDto dto, UsersAuthContext caller)
    {
        var name = UsersValidation.Name("name", dto.Name);
        var email = UsersValidation.Email(dto.Email);
        UsersValidation.HomeDir(dto.HomeDir);
        UsersValidation.Data(dto.Data);
        if (dto.Password != null) _auth.ValidatePassword(dto.Password);

        await _gate.WaitAsync();
        try
        {
            await EnsureUniqueAsync(name, email, exceptUserId: null);

            List<string> groups;
            if (dto.Groups is { Count: > 0 })
            {
                groups = await ResolveGroupNamesAsync(dto.Groups);
                await EnsureCanAssignGroupsAsync(caller, groups);
            }
            else
            {
                groups = [Group.UsersGroup];
            }

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

            await _auth.AuditAsync(AuditEvents.UserCreated, user.Name, null, caller.User.Name,
                userId: user.Id, actorId: caller.User.Id);
            return user;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Updates a user. Callers without <c>users:update</c> may only change their own
    /// <c>email</c> and <c>data</c>. Changing name, groups or disabled requires covering the
    /// target's permissions, and added groups must be covered by the caller.
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

            var nameChanges = dto.Name != null && dto.Name.Trim() != target.Name;
            var groupsChange = dto.Groups != null && !SameSet(dto.Groups, target.Groups);
            var disabledChanges = dto.Disabled.HasValue && dto.Disabled.Value != target.Disabled;
            var homeDirChanges = dto.HomeDir != null && dto.HomeDir != target.HomeDir;

            if (!privileged)
            {
                if (!caller.IsSelf(target))
                    throw UsersApiException.Forbidden($"permission denied: {UsersPermissions.UsersUpdate}");
                if (nameChanges || groupsChange || disabledChanges || homeDirChanges)
                    throw UsersApiException.Forbidden("you may only change your own email and data");
            }

            if (nameChanges || groupsChange || disabledChanges)
                await EnsureCoversTargetAsync(caller, target);

            var update = new UpdateUserDto();

            if (nameChanges) update.Name = UsersValidation.Name("name", dto.Name);
            if (dto.Email != null && dto.Email.Trim() != target.Email) update.Email = UsersValidation.Email(dto.Email);
            if (homeDirChanges)
            {
                UsersValidation.HomeDir(dto.HomeDir);
                update.HomeDir = dto.HomeDir;
            }
            if (dto.Data != null)
            {
                UsersValidation.Data(dto.Data);
                update.Data = dto.Data;
            }
            await EnsureUniqueAsync(update.Name, update.Email, target.Id);

            if (groupsChange)
            {
                update.Groups = dto.Groups!.Count == 0 ? [] : await ResolveGroupNamesAsync(dto.Groups);
                var added = update.Groups.Where(g => !target.IsInGroup(g)).ToList();
                await EnsureCanAssignGroupsAsync(caller, added);
            }
            if (disabledChanges) update.Disabled = dto.Disabled!.Value;

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
            var actorId = caller.User.Id;
            await _auth.AuditAsync(AuditEvents.UserUpdated, updated.Name, null, actor, userId: updated.Id, actorId: actorId);

            if (update.Disabled == true)
            {
                await _auth.AuditAsync(AuditEvents.UserDisabled, updated.Name, null, actor, userId: updated.Id, actorId: actorId);
                await _auth.RevokeUserSessionsAsync(updated.Id, AuthService.RevokeReasons.UserDisabled, actor, actorId: actorId);
            }
            else if (update.Disabled == false)
            {
                await _auth.AuditAsync(AuditEvents.UserEnabled, updated.Name, null, actor, userId: updated.Id, actorId: actorId);
            }

            // After a disable's revoke, so bound event sockets get session-kicked before they close.
            _auth.NotifyPermissionsChanged();
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
            await EnsureCoversTargetAsync(caller, target);
            if (target.IsInGroup(Group.AdminGroup) && !target.Disabled && await IsLastEnabledAdminAsync(target))
                throw UsersApiException.Conflict("cannot delete the last admin");

            await _auth.RevokeUserSessionsAsync(target.Id, AuthService.RevokeReasons.UserDeleted, caller.User.Name,
                actorId: caller.User.Id);
            await _userStore.DeleteUserAsync(target.Id);
            await _credentialStore.DeleteCredentialsAsync(target.Id);
            await _auth.DeleteUserApiTokensAsync(target.Id);
            _auth.ClearLockout(target.Id);

            await _auth.AuditAsync(AuditEvents.UserDeleted, target.Name, null, caller.User.Name,
                userId: target.Id, actorId: caller.User.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Changes a password. Callers with <c>users:update</c> need no current password; self-service
    /// callers without it must supply the correct current password (missing → 400, wrong → 403;
    /// counted toward lockout and audited). A self change clears <c>mustChangePassword</c> and
    /// revokes the caller's other sessions; a reset of another user requires covering their
    /// permissions, clears their lockout, sets <c>mustChangePassword</c> (unless
    /// <c>temporary: false</c>) and revokes all their sessions.
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
        if (!isSelf && caller.User.MustChangePassword)
            throw UsersApiException.Forbidden(AuthService.PasswordChangeRequiredMessage);
        if (!isSelf && !privileged)
            throw UsersApiException.Forbidden($"permission denied: {UsersPermissions.UsersUpdate}");
        if (!isSelf)
            await EnsureCoversTargetAsync(caller, target);

        _auth.ValidatePassword(request.Password);

        if (isSelf && !privileged)
        {
            if (string.IsNullOrEmpty(request.CurrentPassword))
                throw UsersApiException.BadRequest("currentPassword is required");
            if (!await _auth.VerifyPasswordWithLockoutAsync(target, request.CurrentPassword, caller.User.Name, caller.User.Id))
                throw UsersApiException.Forbidden("current password is incorrect");
        }

        await _auth.SetPasswordAsync(target.Id, request.Password!);
        await _userStore.UpdateUserAsync(target.Id, new UpdateUserDto
        {
            MustChangePassword = !isSelf && (request.Temporary ?? true),
        });
        if (!isSelf) _auth.ClearLockout(target.Id);

        await _auth.AuditAsync(AuditEvents.PasswordChanged, target.Name, isSelf ? "changed by self" : "reset",
            caller.User.Name, userId: target.Id, actorId: caller.User.Id);

        await _auth.RevokeUserSessionsAsync(target.Id, AuthService.RevokeReasons.PasswordChanged, caller.User.Name,
            exceptSessionId: isSelf ? caller.Session?.Id : null, actorId: caller.User.Id);
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

    public async Task<Group> CreateGroupAsync(CreateGroupRequest request, UsersAuthContext caller)
    {
        var name = UsersValidation.Name("name", request.Name);
        UsersValidation.Description(request.Description);
        var permissions = UsersValidation.Permissions(request.Permissions) ?? [];
        EnsureCanGrant(caller, permissions);

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

            await _auth.AuditAsync(AuditEvents.GroupCreated, caller.User.Name, $"group '{group.Name}' created",
                caller.User.Name, userId: caller.User.Id, actorId: caller.User.Id);
            return group;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Group> UpdateGroupAsync(string name, UpdateGroupDto dto, UsersAuthContext caller)
    {
        UsersValidation.Description(dto.Description);
        var permissions = UsersValidation.Permissions(dto.Permissions);

        await _gate.WaitAsync();
        try
        {
            var group = await GetGroupOrThrowAsync(name);

            if (permissions != null)
            {
                if (string.Equals(group.Name, Group.AdminGroup, StringComparison.OrdinalIgnoreCase) &&
                    !SameSet(permissions, group.Permissions))
                {
                    throw UsersApiException.Conflict("the admin group's permissions cannot be changed");
                }
                EnsureCanGrant(caller, permissions);
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

            _auth.NotifyPermissionsChanged();
            await _auth.AuditAsync(AuditEvents.GroupUpdated, caller.User.Name, $"group '{updated.Name}' updated",
                caller.User.Name, userId: caller.User.Id, actorId: caller.User.Id);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteGroupAsync(string name, UsersAuthContext caller)
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
            _auth.NotifyPermissionsChanged();
            await _auth.AuditAsync(AuditEvents.GroupDeleted, caller.User.Name, $"group '{group.Name}' deleted",
                caller.User.Name, userId: caller.User.Id, actorId: caller.User.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddMemberAsync(string groupName, string idOrName, UsersAuthContext caller)
    {
        await _gate.WaitAsync();
        try
        {
            var group = await _groupStore.GetGroupAsync(groupName)
                ?? throw UsersApiException.BadRequest($"unknown group: {UsersValidation.Sanitize(groupName, 64)}");
            var user = await GetUserOrThrowAsync(idOrName);

            if (user.IsInGroup(group.Name)) return;

            EnsureCanGrant(caller, group.Permissions);
            await EnsureCoversTargetAsync(caller, user);

            await _userStore.UpdateUserAsync(user.Id, new UpdateUserDto { Groups = [.. user.Groups, group.Name] });
            _auth.NotifyPermissionsChanged();
            await _auth.AuditAsync(AuditEvents.GroupMemberAdded, user.Name, $"added to group '{group.Name}'",
                caller.User.Name, userId: user.Id, actorId: caller.User.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveMemberAsync(string groupName, string idOrName, UsersAuthContext caller)
    {
        await _gate.WaitAsync();
        try
        {
            var group = await GetGroupOrThrowAsync(groupName);
            var user = await GetUserOrThrowAsync(idOrName);

            if (!user.IsInGroup(group.Name)) return;

            await EnsureCoversTargetAsync(caller, user);

            if (string.Equals(group.Name, Group.AdminGroup, StringComparison.OrdinalIgnoreCase) &&
                !user.Disabled && await IsLastEnabledAdminAsync(user))
            {
                throw UsersApiException.Conflict("cannot remove the last admin from the admin group");
            }

            await _userStore.UpdateUserAsync(user.Id, new UpdateUserDto
            {
                Groups = user.Groups.Where(g => !string.Equals(g, group.Name, StringComparison.OrdinalIgnoreCase)).ToList(),
            });
            _auth.NotifyPermissionsChanged();
            await _auth.AuditAsync(AuditEvents.GroupMemberRemoved, user.Name, $"removed from group '{group.Name}'",
                caller.User.Name, userId: user.Id, actorId: caller.User.Id);
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

    public async Task ClearAuditAsync(UsersAuthContext caller)
    {
        await _auditStore.ClearAsync();
        await _auth.AuditAsync(AuditEvents.AuditCleared, caller.User.Name, "audit log cleared", caller.User.Name,
            userId: caller.User.Id, actorId: caller.User.Id);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private async Task<bool> IsLastEnabledAdminAsync(User user)
    {
        var users = await _userStore.GetUsersAsync();
        return !users.Any(u => u.Id != user.Id && !u.Disabled && u.IsInGroup(Group.AdminGroup));
    }

    /// <summary>
    /// Names and emails share one uniqueness namespace (case-insensitive): a name may not equal
    /// another user's name or email, and an email may not equal another user's email or name.
    /// </summary>
    private async Task EnsureUniqueAsync(string? name, string? email, string? exceptUserId)
    {
        if (name == null && email == null) return;
        var users = await _userStore.GetUsersAsync();
        foreach (var u in users.Where(u => u.Id != exceptUserId))
        {
            if (name != null && (Eq(u.Name, name) || Eq(u.Email, name)))
                throw UsersApiException.Conflict($"user '{name}' already exists");
            if (email != null && (Eq(u.Email, email) || Eq(u.Name, email)))
                throw UsersApiException.Conflict($"email '{email}' is already in use");
        }
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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
                ?? throw UsersApiException.BadRequest($"unknown group: {UsersValidation.Sanitize(name, 64)}");
            if (!result.Any(g => string.Equals(g, group.Name, StringComparison.OrdinalIgnoreCase)))
                result.Add(group.Name);
        }
        return result;
    }

    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) =>
        new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b);
}
