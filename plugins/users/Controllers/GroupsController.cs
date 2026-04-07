using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.Controllers;

/// <summary>
/// Group CRUD and member management endpoints.
/// </summary>
[ApiController]
[Route("api/v1/qcli/groups")]
public class GroupsController : ControllerBase
{
    private readonly IGroupStore _groupStore;
    private readonly IUserStore _userStore;

    public GroupsController(IGroupStore groupStore, IUserStore userStore)
    {
        _groupStore = groupStore;
        _userStore = userStore;
    }

    /// <summary>
    /// List all groups.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _groupStore.GetGroupsAsync());

    /// <summary>
    /// Get a group by name.
    /// </summary>
    [HttpGet("{name}")]
    public async Task<IActionResult> Get(string name)
    {
        var group = await _groupStore.GetGroupAsync(name);
        if (group == null)
            return NotFound(new { error = "Group not found" });
        return Ok(group);
    }

    /// <summary>
    /// Create a new group (admin only).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateGroupRequest request)
    {
        if (!HttpContext.IsUsersAdmin())
            return StatusCode(403, new { error = "Forbidden: admin access required" });

        if (string.IsNullOrEmpty(request.Name))
            return BadRequest(new { error = "name is required" });

        try
        {
            var group = await _groupStore.CreateGroupAsync(request.Name, request.Description);
            return StatusCode(201, group);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a group (admin only).
    /// </summary>
    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name)
    {
        if (!HttpContext.IsUsersAdmin())
            return StatusCode(403, new { error = "Forbidden: admin access required" });

        await _groupStore.DeleteGroupAsync(name);
        return NoContent();
    }

    /// <summary>
    /// Add a user to a group (admin only).
    /// </summary>
    [HttpPut("{name}/members/{userId}")]
    public async Task<IActionResult> AddMember(string name, string userId)
    {
        if (!HttpContext.IsUsersAdmin())
            return StatusCode(403, new { error = "Forbidden: admin access required" });

        var group = await _groupStore.GetGroupAsync(name);
        if (group == null)
            return NotFound(new { error = "Group not found" });

        var user = await _userStore.GetUserAsync(userId);
        if (user == null)
            return NotFound(new { error = "User not found" });

        if (!user.Groups.Contains(name))
        {
            await _userStore.UpdateUserAsync(userId, new UpdateUserDto
            {
                Groups = [.. user.Groups, name],
            });
        }

        return Ok(new { message = $"User added to group '{name}'" });
    }

    /// <summary>
    /// Remove a user from a group (admin only).
    /// </summary>
    [HttpDelete("{name}/members/{userId}")]
    public async Task<IActionResult> RemoveMember(string name, string userId)
    {
        if (!HttpContext.IsUsersAdmin())
            return StatusCode(403, new { error = "Forbidden: admin access required" });

        var user = await _userStore.GetUserAsync(userId);
        if (user == null)
            return NotFound(new { error = "User not found" });

        await _userStore.UpdateUserAsync(userId, new UpdateUserDto
        {
            Groups = user.Groups.Where(g => g != name).ToList(),
        });

        return Ok(new { message = $"User removed from group '{name}'" });
    }
}
