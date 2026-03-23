using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Qodalis.Cli.Plugin.Admin.Services;

namespace Qodalis.Cli.Plugin.Admin.Auth;

/// <summary>
/// Handles JWT token generation and validation using HMAC-SHA256.
/// </summary>
public class JwtService
{
    private readonly AdminConfig _config;
    private readonly ILogger<JwtService> _logger;
    private readonly SigningCredentials _signingCredentials;
    private readonly TokenValidationParameters _validationParameters;

    /// <summary>
    /// Initializes a new instance of the <see cref="JwtService"/> class.
    /// </summary>
    /// <param name="config">The admin configuration containing the JWT secret and expiry settings.</param>
    /// <param name="logger">The logger instance.</param>
    public JwtService(AdminConfig config, ILogger<JwtService> logger)
    {
        _config = config;
        _logger = logger;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config.JwtSecret));
        _signingCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateIssuer = true,
            ValidIssuer = "qcli-admin",
            ValidateAudience = true,
            ValidAudience = "qcli-admin",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    }

    /// <summary>
    /// Generates a signed JWT token for the specified admin user.
    /// </summary>
    /// <param name="username">The username to include in the token claims.</param>
    /// <returns>The serialized JWT token string.</returns>
    public string GenerateToken(string username)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new Claim("authenticated_at", DateTime.UtcNow.ToString("o")),
        };

        var token = new JwtSecurityToken(
            issuer: "qcli-admin",
            audience: "qcli-admin",
            claims: claims,
            expires: DateTime.UtcNow.Add(_config.JwtExpiry),
            signingCredentials: _signingCredentials);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);
        _logger.LogDebug("Generated JWT token for user {Username}, expires {Expiry}", username, token.ValidTo);
        return tokenString;
    }

    /// <summary>
    /// Validates a JWT token and returns the claims principal if valid.
    /// </summary>
    /// <param name="token">The JWT token string to validate.</param>
    /// <returns>The <see cref="ClaimsPrincipal"/> if the token is valid; otherwise, null.</returns>
    public ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, _validationParameters, out _);
            return principal;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Token validation failed: {Error}", ex.Message);
            return null;
        }
    }
}
