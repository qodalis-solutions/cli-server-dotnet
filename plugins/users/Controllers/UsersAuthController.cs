using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;

namespace Qodalis.Cli.Plugin.Users.Controllers;

/// <summary>
/// Authentication endpoints: login, logout, verify.
/// </summary>
[ApiController]
[Route("api/v1/qcli/auth")]
public class UsersAuthController : ControllerBase
{
    private readonly AuthService _authService;

    public UsersAuthController(AuthService authService)
    {
        _authService = authService;
    }

    /// <summary>
    /// Authenticates a user and returns a bearer token with session info.
    /// No auth required.
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
            return BadRequest(new { error = "username and password are required" });

        try
        {
            var metadata = new SessionMetadata
            {
                RemoteAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = HttpContext.Request.Headers.UserAgent.FirstOrDefault(),
            };
            var result = await _authService.LoginAsync(request.Username, request.Password, metadata);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Logs out the current session. Auth required.
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var session = HttpContext.GetUsersSession();
        if (session == null)
            return Unauthorized(new { error = "Unauthorized" });

        var authHeader = HttpContext.Request.Headers.Authorization.FirstOrDefault() ?? "";
        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : "";

        await _authService.LogoutAsync(token);
        return NoContent();
    }

    /// <summary>
    /// Verifies a user's password. Returns { valid: true/false }.
    /// </summary>
    [HttpPost("verify")]
    public async Task<IActionResult> Verify([FromBody] VerifyRequest request)
    {
        if (string.IsNullOrEmpty(request.UserId) || string.IsNullOrEmpty(request.Password))
            return BadRequest(new { error = "userId and password are required" });

        var valid = await _authService.VerifyPasswordAsync(request.UserId, request.Password);
        return Ok(new VerifyResponse { Valid = valid });
    }
}
