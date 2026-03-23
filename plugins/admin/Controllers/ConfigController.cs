using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Admin.Services;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

/// <summary>
/// Admin controller for viewing and updating server configuration settings.
/// </summary>
[ApiController]
[Route("api/v1/qcli/config")]
public class ConfigController : ControllerBase
{
    private readonly AdminConfig _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigController"/> class.
    /// </summary>
    public ConfigController(AdminConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Returns all configuration sections with their current settings.
    /// </summary>
    [HttpGet]
    public IActionResult GetConfig()
    {
        return Ok(new { sections = _config.GetConfigSections() });
    }

    /// <summary>
    /// Updates mutable configuration settings (e.g., auth credentials, JWT expiry).
    /// </summary>
    /// <param name="request">The configuration update request.</param>
    [HttpPut]
    public IActionResult UpdateConfig([FromBody] UpdateConfigRequest request)
    {
        if (request.Auth != null)
        {
            if (!string.IsNullOrEmpty(request.Auth.Username))
                _config.Username = request.Auth.Username;

            if (!string.IsNullOrEmpty(request.Auth.Password))
                _config.Password = request.Auth.Password;

            if (request.Auth.JwtExpiryHours.HasValue && request.Auth.JwtExpiryHours.Value > 0)
                _config.JwtExpiry = TimeSpan.FromHours(request.Auth.JwtExpiryHours.Value);
        }

        return Ok(new { message = "Configuration updated" });
    }
}

/// <summary>
/// Request body for updating server configuration.
/// </summary>
public class UpdateConfigRequest
{
    /// <summary>Authentication-related configuration updates.</summary>
    public AuthConfigUpdate? Auth { get; set; }
}

/// <summary>
/// Authentication configuration update fields.
/// </summary>
public class AuthConfigUpdate
{
    /// <summary>New admin username.</summary>
    public string? Username { get; set; }
    /// <summary>New admin password.</summary>
    public string? Password { get; set; }
    /// <summary>New JWT token expiry in hours.</summary>
    public double? JwtExpiryHours { get; set; }
}
