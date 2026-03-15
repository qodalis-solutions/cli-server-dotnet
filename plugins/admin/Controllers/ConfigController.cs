using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Admin.Services;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

[ApiController]
[Route("api/v1/qcli/config")]
public class ConfigController : ControllerBase
{
    private readonly AdminConfig _config;

    public ConfigController(AdminConfig config)
    {
        _config = config;
    }

    [HttpGet]
    public IActionResult GetConfig()
    {
        return Ok(_config.GetConfigSections());
    }

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

public class UpdateConfigRequest
{
    public AuthConfigUpdate? Auth { get; set; }
}

public class AuthConfigUpdate
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public double? JwtExpiryHours { get; set; }
}
