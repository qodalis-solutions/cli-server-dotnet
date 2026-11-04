using System.Collections.Concurrent;
using System.Security.Claims;

using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Admin.Auth;

/// <summary>
/// Authentication controller for admin login with rate limiting and JWT token issuance.
/// </summary>
[ApiController]
[Route("api/v1/qcli/auth")]
public class AuthController : ControllerBase
{
    private readonly AdminConfig _config;
    private readonly JwtService _jwtService;
    private readonly ICliAuthBridge? _bridge;
    private readonly AdminAuthMode? _authMode;

    // Rate limiting: track login attempts per IP
    private static readonly ConcurrentDictionary<string, List<DateTime>> _loginAttempts = new();
    private const int MaxAttemptsPerMinute = 5;
    private const int WindowSeconds = 60;

    // Periodic cleanup of expired rate-limit entries to prevent memory leaks
    private static readonly Timer _cleanupTimer = new(_ =>
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-WindowSeconds);
        foreach (var kvp in _loginAttempts)
        {
            lock (kvp.Value)
            {
                kvp.Value.RemoveAll(a => a <= cutoff);
                if (kvp.Value.Count == 0)
                    _loginAttempts.TryRemove(kvp.Key, out List<DateTime>? _);
            }
        }
    }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthController"/> class.
    /// </summary>
    /// <param name="bridge">The users-plugin auth bridge, when installed.</param>
    /// <param name="authMode">Password or external sign-in (registered by <c>AddAdmin</c>).</param>
    public AuthController(AdminConfig config, JwtService jwtService, ICliAuthBridge? bridge = null,
        AdminAuthMode? authMode = null)
    {
        _bridge = bridge;
        _authMode = authMode;
        _config = config;
        _jwtService = jwtService;
    }

    /// <summary>
    /// Which sign-in the dashboard shows. Public. <c>external</c> when the users plugin uses an
    /// external auth adapter (the host application signs users in and hands its token to the
    /// dashboard through <c>/qcli/admin/#token=…</c>), otherwise <c>password</c>.
    /// </summary>
    [HttpGet("mode")]
    public IActionResult Mode()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new
        {
            mode = _authMode?.Mode ?? AdminAuthMode.Password,
            signInUrl = _config.SignInUrl,
        });
    }

    /// <summary>
    /// Authenticates an admin user and returns a JWT token. Rate-limited per IP.
    /// Answers 501 in external sign-in mode: the static admin credentials sign no one in there.
    /// </summary>
    /// <param name="request">The login credentials.</param>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (_authMode?.IsExternal == true)
        {
            var where = _config.SignInUrl != null ? $" at {_config.SignInUrl}" : " through the host application";
            return StatusCode(501, new
            {
                error = $"Sign-in is managed by the host application. Sign in{where}.",
                code = "EXTERNAL_AUTH",
                signInUrl = _config.SignInUrl,
            });
        }

        // When the users plugin is installed the dashboard authenticates through it
        // (lockout, rate limiting, audit) and requires the admin:access permission;
        // the static admin credentials are only used without the users plugin.
        var bridge = _bridge;
        if (bridge != null)
        {
            CliAuthLogin login;
            try
            {
                login = await bridge.LoginAsync(HttpContext, request.Username, request.Password);
            }
            catch (CliAuthException ex)
            {
                return StatusCode(ex.StatusCode, new { error = ex.Message, code = ex.StatusCode == 429 ? "RATE_LIMITED" : "INVALID_CREDENTIALS" });
            }

            if (!login.Principal.Can(CliRoutePolicy.AdminAccess))
            {
                await bridge.LogoutAsync(HttpContext, login.Token);
                return StatusCode(403, new { error = $"permission denied: {CliRoutePolicy.AdminAccess}", code = "FORBIDDEN" });
            }

            return Ok(new
            {
                token = login.Token,
                expiresIn = login.ExpiresInSeconds,
                username = login.Principal.UserName,
            });
        }

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

    /// <summary>
    /// Returns information about the currently authenticated admin user.
    /// </summary>
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

        lock (attempts)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-WindowSeconds);
            return attempts.Count(a => a > cutoff) >= MaxAttemptsPerMinute;
        }
    }

    private static void RecordAttempt(string ip)
    {
        var attempts = _loginAttempts.GetOrAdd(ip, _ => new List<DateTime>());
        lock (attempts)
        {
            // Clean up old entries
            var cutoff = DateTime.UtcNow.AddSeconds(-WindowSeconds);
            attempts.RemoveAll(a => a <= cutoff);
            attempts.Add(DateTime.UtcNow);
        }
    }
}

/// <summary>
/// Request body for admin login.
/// </summary>
public class LoginRequest
{
    /// <summary>The admin username.</summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>The admin password.</summary>
    public string Password { get; set; } = string.Empty;
}
