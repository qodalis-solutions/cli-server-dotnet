using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.Controllers;

/// <summary>
/// User CRUD and password management endpoints.
/// </summary>
[ApiController]
[Route("api/v1/qcli/users")]
public class UsersController : ControllerBase
{
    private readonly IUserStore _userStore;
    private readonly AuthService _authService;
    private readonly ICredentialStore _credentialStore;
    private readonly ISessionStore _sessionStore;
    private readonly IExternalAuthAdapter? _authAdapter;

    public UsersController(
        IUserStore userStore,
        AuthService authService,
        ICredentialStore credentialStore,
        ISessionStore sessionStore,
        IExternalAuthAdapter? authAdapter = null)
    {
        _userStore = userStore;
        _authService = authService;
        _credentialStore = credentialStore;
        _sessionStore = sessionStore;
        _authAdapter = authAdapter;
    }

    /// <summary>
    /// List all users.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _userStore.GetUsersAsync());

    /// <summary>
    /// Get a user by id or name.
    /// </summary>
    [HttpGet("{idOrName}")]
    public async Task<IActionResult> Get(string idOrName)
    {
        var user = await _userStore.GetUserAsync(idOrName);
        if (user == null)
            return NotFound(new { error = "User not found" });
        return Ok(user);
    }

    /// <summary>
    /// Create a new user (admin only).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserDto dto)
    {
        if (!HttpContext.IsUsersAdmin())
            return StatusCode(403, new { error = "Forbidden: admin access required" });

        if (string.IsNullOrEmpty(dto.Name) || string.IsNullOrEmpty(dto.Email))
            return BadRequest(new { error = "name and email are required" });

        try
        {
            var user = await _userStore.CreateUserAsync(dto);
            if (!string.IsNullOrEmpty(dto.Password))
                await _authService.SetPasswordAsync(user.Id, dto.Password);
            return StatusCode(201, user);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update a user (admin only).
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateUserDto dto)
    {
        if (!HttpContext.IsUsersAdmin())
            return StatusCode(403, new { error = "Forbidden: admin access required" });

        try
        {
            var user = await _userStore.UpdateUserAsync(id, dto);
            return Ok(user);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a user (admin only). Cascades credentials and sessions.
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (!HttpContext.IsUsersAdmin())
            return StatusCode(403, new { error = "Forbidden: admin access required" });

        await _userStore.DeleteUserAsync(id);
        await _credentialStore.DeleteCredentialsAsync(id);
        await _sessionStore.DeleteUserSessionsAsync(id);
        return NoContent();
    }

    /// <summary>
    /// Change a user's password (self or admin).
    /// </summary>
    [HttpPut("{id}/password")]
    public async Task<IActionResult> ChangePassword(string id, [FromBody] PasswordChangeRequest request)
    {
        var session = HttpContext.GetUsersSession();
        if (session == null)
            return Unauthorized(new { error = "Unauthorized" });

        var isSelf = session.User.Id == id;
        var isAdmin = HttpContext.IsUsersAdmin();

        if (!isSelf && !isAdmin)
            return StatusCode(403, new { error = "Forbidden" });

        if (string.IsNullOrEmpty(request.NewPassword))
            return BadRequest(new { error = "newPassword is required" });

        if (isSelf && !isAdmin)
        {
            if (string.IsNullOrEmpty(request.CurrentPassword))
                return BadRequest(new { error = "currentPassword is required" });

            var valid = await _authService.VerifyPasswordAsync(id, request.CurrentPassword);
            // 403, not 401: the session is valid, the supplied password is not
            if (!valid)
                return StatusCode(403, new { error = "Current password is incorrect" });
        }

        await _authService.SetPasswordAsync(id, request.NewPassword);
        return Ok(new { message = "Password updated" });
    }

    /// <summary>
    /// Verify a user's password. Returns <c>{ valid: true/false }</c>.
    /// Requires an authenticated session (used by <c>su</c> and <c>passwd</c> on the client).
    /// </summary>
    [HttpPost("{id}/verify-password")]
    public async Task<IActionResult> VerifyPassword(string id, [FromBody] VerifyPasswordRequest request)
    {
        if (_authAdapter != null)
            return StatusCode(501, new { error = "Authentication is managed by the host application" });

        if (HttpContext.GetUsersSession() == null)
            return Unauthorized(new { error = "Unauthorized" });

        if (string.IsNullOrEmpty(request.Password))
            return BadRequest(new { error = "password is required" });

        var valid = await _authService.VerifyPasswordAsync(id, request.Password);
        return Ok(new VerifyResponse { Valid = valid });
    }
}
