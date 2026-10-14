using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;

namespace Qodalis.Cli.Plugin.Users.Controllers;

/// <summary>
/// Groups resource: CRUD (with permissions) and membership management.
/// </summary>
[Route("api/v1/qcli/groups")]
public class GroupsController : UsersControllerBase
{
    private readonly UsersService _users;

    public GroupsController(UsersService users)
    {
        _users = users;
    }

    /// <summary>List all groups. Any authenticated user.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll() => JsonOk(await _users.ListGroupsAsync());

    /// <summary>Get a group by name (case-insensitive). Any authenticated user.</summary>
    [HttpGet("{name}")]
    public async Task<IActionResult> Get([FromRoute] string name) =>
        JsonOk(await _users.GetGroupOrThrowAsync(name));

    /// <summary>List the members of a group. Requires <c>groups:read</c> or membership.</summary>
    [HttpGet("{name}/members")]
    public async Task<IActionResult> GetMembers([FromRoute] string name)
    {
        var group = await _users.GetGroupOrThrowAsync(name);
        var caller = Auth;
        if (!caller.Can(UsersPermissions.GroupsRead) && !caller.User.IsInGroup(group.Name))
            throw Denied(UsersPermissions.GroupsRead);
        return JsonOk(await _users.GetGroupMembersAsync(group));
    }

    /// <summary>Create a group. Requires <c>groups:create</c>.</summary>
    [HttpPost]
    [UsersPermission(UsersPermissions.GroupsCreate)]
    public async Task<IActionResult> Create([FromBody] CreateGroupRequest request) =>
        Json(201, await _users.CreateGroupAsync(request, Auth.User.Name));

    /// <summary>Update a group's description/permissions. Requires <c>groups:update</c>.</summary>
    [HttpPut("{name}")]
    [UsersPermission(UsersPermissions.GroupsUpdate)]
    public async Task<IActionResult> Update([FromRoute] string name, [FromBody] UpdateGroupDto dto) =>
        JsonOk(await _users.UpdateGroupAsync(name, dto, Auth.User.Name));

    /// <summary>Delete a group (removed from all users). Requires <c>groups:delete</c>.</summary>
    [HttpDelete("{name}")]
    [UsersPermission(UsersPermissions.GroupsDelete)]
    public async Task<IActionResult> Delete([FromRoute] string name)
    {
        await _users.DeleteGroupAsync(name, Auth.User.Name);
        return NoContent();
    }

    /// <summary>Add a user to a group. Requires <c>groups:update</c>.</summary>
    [HttpPost("{name}/members/{idOrName}")]
    [UsersPermission(UsersPermissions.GroupsUpdate)]
    public async Task<IActionResult> AddMember([FromRoute] string name, [FromRoute] string idOrName)
    {
        await _users.AddMemberAsync(name, idOrName, Auth.User.Name);
        return NoContent();
    }

    /// <summary>Remove a user from a group. Requires <c>groups:update</c>.</summary>
    [HttpDelete("{name}/members/{idOrName}")]
    [UsersPermission(UsersPermissions.GroupsUpdate)]
    public async Task<IActionResult> RemoveMember([FromRoute] string name, [FromRoute] string idOrName)
    {
        await _users.RemoveMemberAsync(name, idOrName, Auth.User.Name);
        return NoContent();
    }
}
