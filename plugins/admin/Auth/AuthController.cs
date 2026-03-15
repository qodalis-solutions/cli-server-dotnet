using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Admin.Services;

namespace Qodalis.Cli.Plugin.Admin.Auth;

[ApiController]
[Route("api/v1/qcli/auth")]
public class AuthController : ControllerBase
{
    private readonly AdminConfig _config;
    private readonly JwtService _jwtService;

    // Rate limiting: track login attempts per IP
    private static readonly ConcurrentDictionary<string, List<DateTime>> _loginAttempts = new();
    private const int MaxAttemptsPerMinute = 5;

    public AuthController(AdminConfig config, JwtService jwtService)
    {
        _config = config;
        _jwtService = jwtService;
    }

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Rate limiting
        if (IsRateLimited(ip))
        {
            return StatusCode(429, new { error = "Too many login attempts. Try again later.", code = "RATE_LIMITED" });
        }

        if (!_config.ValidateCredentials(request.Username, request.Password))
        {
            RecordAttempt(ip);
            return Unauthorized(new { error = "Invalid credentials", code = "INVALID_CREDENTIALS" });
        }

        var token = _jwtService.GenerateToken(request.Username);

        return Ok(new
        {
            token,
            expiresIn = (int)_config.JwtExpiry.TotalSeconds,
            username = request.Username,
        });
    }

    [HttpGet("me")]
    public IActionResult Me()
    {
        if (HttpContext.Items["AdminUser"] is not ClaimsPrincipal principal)
        {
            return Unauthorized(new { error = "Not authenticated", code = "UNAUTHORIZED" });
        }

        var username = principal.FindFirst(ClaimTypes.Name)?.Value ?? "unknown";
        var authenticatedAt = principal.FindFirst("authenticated_at")?.Value;

        return Ok(new
        {
            username,
            authenticatedAt,
        });
    }

    private static bool IsRateLimited(string ip)
    {
        if (!_loginAttempts.TryGetValue(ip, out var attempts))
            return false;

        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        var recentAttempts = attempts.Count(a => a > cutoff);
        return recentAttempts >= MaxAttemptsPerMinute;
    }

    private static void RecordAttempt(string ip)
    {
        var attempts = _loginAttempts.GetOrAdd(ip, _ => new List<DateTime>());
        lock (attempts)
        {
            // Clean up old entries
            var cutoff = DateTime.UtcNow.AddMinutes(-1);
            attempts.RemoveAll(a => a <= cutoff);
            attempts.Add(DateTime.UtcNow);
        }
    }
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
