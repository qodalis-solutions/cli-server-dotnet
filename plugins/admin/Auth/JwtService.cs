using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Qodalis.Cli.Plugin.Admin.Services;

namespace Qodalis.Cli.Plugin.Admin.Auth;

/// <summary>
/// Handles JWT token generation and validation using HMAC-SHA256.
/// </summary>
public class JwtService
{
    private readonly AdminConfig _config;
    private readonly SigningCredentials _signingCredentials;
    private readonly TokenValidationParameters _validationParameters;

    public JwtService(AdminConfig config)
    {
        _config = config;

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

    public string GenerateToken(string username)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: "qcli-admin",
            audience: "qcli-admin",
            claims: claims,
            expires: DateTime.UtcNow.Add(_config.JwtExpiry),
            signingCredentials: _signingCredentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, _validationParameters, out _);
            return principal;
        }
        catch
        {
            return null;
        }
    }
}
