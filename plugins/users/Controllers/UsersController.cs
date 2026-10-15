using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;

namespace Qodalis.Cli.Plugin.Users.Controllers;

/// <summary>
/// Users resource: CRUD, effective permissions, password change and verification.
/// </summary>
[Route("api/v1/qcli/users")]
public class UsersController : UsersControllerBase
{
    private readonly UsersService _users;
    private readonly AuthService _auth;
    private readonly IpRateLimiter _limiter;

    public UsersController(UsersService users, AuthService auth, IpRateLimiter limiter)
    {
        _users = users;
        _auth = auth;
        _limiter = limiter;
    }

    /// <summary>List users (<c>?query=&amp;skip=&amp;take=</c>). Requires <c>users:read</c>.</summary>
    [HttpGet]
    [UsersPermission(UsersPermissions.UsersRead)]
    public async Task<IActionResult> GetAll([FromQuery] string? query, [FromQuery] int? skip, [FromQuery] int? take) =>
        JsonOk(await _users.ListUsersAsync(query, skip, take));

    /// <summary>Get a user by id or name. Requires <c>users:read</c> or self.</summary>
    [HttpGet("{idOrName}")]
    public async Task<IActionResult> Get([FromRoute] string idOrName) =>
        JsonOk(await ResolveReadableUserAsync(idOrName));

    /// <summary>Effective permissions of a user. Requires <c>users:read</c> or self.</summary>
    [HttpGet("{idOrName}/permissions")]
    public async Task<IActionResult> GetPermissions([FromRoute] string idOrName) =>
        JsonOk(await _users.GetUserPermissionsAsync(await ResolveReadableUserAsync(idOrName)));

    /// <summary>Create a user. Requires <c>users:create</c>.</summary>
    [HttpPost]
    [UsersPermission(UsersPermissions.UsersCreate)]
    public async Task<IActionResult> Create([FromBody] CreateUserDto dto) =>
        Json(201, await _users.CreateUserAsync(dto, Auth));

    /// <summary>Update a user. Requires <c>users:update</c>; self may change only email and data.</summary>
    [HttpPut("{idOrName}")]
    public async Task<IActionResult> Update([FromRoute] string idOrName, [FromBody] UpdateUserDto dto) =>
        JsonOk(await _users.UpdateUserAsync(idOrName, dto, Auth));

    /// <summary>Delete a user (cascades credentials and sessions). Requires <c>users:delete</c>.</summary>
    [HttpDelete("{idOrName}")]
    [UsersPermission(UsersPermissions.UsersDelete)]
    public async Task<IActionResult> Delete([FromRoute] string idOrName)
    {
        await _users.DeleteUserAsync(idOrName, Auth);
        return NoContent();
    }

    /// <summary>Change a password. Self (with current password) or <c>users:update</c>.</summary>
    [HttpPut("{idOrName}/password")]
    [UsersCredentialEndpoint]
    [UsersAllowPasswordChangeRequired]
    public async Task<IActionResult> ChangePassword([FromRoute] string idOrName, [FromBody] PasswordChangeRequest request)
    {
        var caller = Auth;
        var target = await _users.FindUserAsync(idOrName);
        if (target != null && caller.IsSelf(target))
            _limiter.Acquire(HttpContext);
        await _users.ChangePasswordAsync(idOrName, request, caller);
        return NoContent();
    }

    /// <summary>
    /// Verify a user's password → <c>{ valid }</c>. Self or <c>users:update</c>.
    /// Failures count toward lockout and are audited.
    /// </summary>
    [HttpPost("{idOrName}/verify-password")]
    [UsersCredentialEndpoint]
    public async Task<IActionResult> VerifyPassword([FromRoute] string idOrName, [FromBody] VerifyPasswordRequest request)
    {
        var caller = Auth;
        var target = await _users.FindUserAsync(idOrName);
        var privileged = caller.Can(UsersPermissions.UsersUpdate);
        if (target == null)
            throw privileged ? UsersApiException.NotFound("user not found") : Denied(UsersPermissions.UsersUpdate);
        if (!privileged && !caller.IsSelf(target))
            throw Denied(UsersPermissions.UsersUpdate);
        if (string.IsNullOrEmpty(request.Password))
            throw UsersApiException.BadRequest("password is required");
        if (request.Password.Length > _auth.Options.MaxPasswordLength)
            throw UsersApiException.BadRequest("password is too long");

        _limiter.Acquire(HttpContext);
        var valid = await _auth.VerifyPasswordWithLockoutAsync(target, request.Password, caller.User.Name, caller.User.Id);
        return JsonOk(new VerifyResponse { Valid = valid });
    }

    private async Task<User> ResolveReadableUserAsync(string idOrName)
    {
        var caller = Auth;
        var privileged = caller.Can(UsersPermissions.UsersRead);
        var user = await _users.FindUserAsync(idOrName);
        if (user == null)
            throw privileged ? UsersApiException.NotFound("user not found") : Denied(UsersPermissions.UsersRead);
        if (!privileged && !caller.IsSelf(user))
            throw Denied(UsersPermissions.UsersRead);
        return user;
    }
}
