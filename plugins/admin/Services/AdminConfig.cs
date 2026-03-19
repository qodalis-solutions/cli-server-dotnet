using System.Security.Cryptography;
using System.Text;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>
/// Admin configuration — reads credentials and JWT settings from environment variables.
/// </summary>
public class AdminConfig
{
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "admin";
    public string JwtSecret { get; set; } = string.Empty;
    public TimeSpan JwtExpiry { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Optional explicit path to the dashboard dist directory.</summary>
    public string? DashboardPath { get; set; }

    /// <summary>
    /// Resolves configuration from environment variables, falling back to configured defaults.
    /// </summary>
    internal void ResolveFromEnvironment()
    {
        var envUser = Environment.GetEnvironmentVariable("QCLI_ADMIN_USERNAME");
        if (!string.IsNullOrEmpty(envUser)) Username = envUser;

        var envPass = Environment.GetEnvironmentVariable("QCLI_ADMIN_PASSWORD");
        if (!string.IsNullOrEmpty(envPass)) Password = envPass;

        var envSecret = Environment.GetEnvironmentVariable("QCLI_ADMIN_JWT_SECRET");
        if (!string.IsNullOrEmpty(envSecret))
        {
            JwtSecret = envSecret;
        }
        else if (string.IsNullOrEmpty(JwtSecret))
        {
            // Generate a random 256-bit secret
            var bytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            JwtSecret = Convert.ToBase64String(bytes);
        }

        if (Username == "admin" && Password == "admin")
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[qcli-admin] WARNING: Using default admin credentials. Set QCLI_ADMIN_USERNAME and QCLI_ADMIN_PASSWORD environment variables.");
            Console.ResetColor();
        }
    }

    public bool ValidateCredentials(string username, string password)
    {
        var usernameMatch = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(username),
            Encoding.UTF8.GetBytes(Username));
        var passwordMatch = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(password),
            Encoding.UTF8.GetBytes(Password));
        return usernameMatch && passwordMatch;
    }

    public object[] GetConfigSections()
    {
        return new object[]
        {
            new
            {
                name = "server",
                mutable = false,
                settings = new object[]
                {
                    new { key = "platform", value = "dotnet", type = "string", description = "Server platform", mutable = false },
                    new { key = "platformVersion", value = Environment.Version.ToString(), type = "string", description = ".NET version", mutable = false },
                    new { key = "os", value = Environment.OSVersion.ToString(), type = "string", description = "Operating system", mutable = false },
                },
            },
            new
            {
                name = "auth",
                mutable = false,
                settings = new object[]
                {
                    new { key = "username", value = Username, type = "string", description = "Admin username", mutable = false },
                    new { key = "jwtExpiryHours", value = JwtExpiry.TotalHours, type = "number", description = "JWT token expiry (hours)", mutable = false },
                    new { key = "jwtSecretConfigured", value = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QCLI_ADMIN_JWT_SECRET")), type = "boolean", description = "Whether JWT secret is explicitly set", mutable = false },
                },
            },
            new
            {
                name = "environment",
                mutable = false,
                settings = new object[]
                {
                    new { key = "QCLI_ADMIN_USERNAME", value = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QCLI_ADMIN_USERNAME")) ? "(set)" : "(default)", type = "string", description = "Admin username env var", mutable = false },
                    new { key = "QCLI_ADMIN_PASSWORD", value = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QCLI_ADMIN_PASSWORD")) ? "(set)" : "(default)", type = "string", description = "Admin password env var", mutable = false },
                    new { key = "QCLI_ADMIN_JWT_SECRET", value = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QCLI_ADMIN_JWT_SECRET")) ? "(set)" : "(auto-generated)", type = "string", description = "JWT secret env var", mutable = false },
                },
            },
        };
    }
}
