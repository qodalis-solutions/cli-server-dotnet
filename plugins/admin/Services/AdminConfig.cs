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
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            JwtSecret = Convert.ToBase64String(bytes);
        }
    }

    public bool ValidateCredentials(string username, string password)
    {
        return string.Equals(Username, username, StringComparison.Ordinal)
            && string.Equals(Password, password, StringComparison.Ordinal);
    }

    public object GetConfigSections()
    {
        return new
        {
            server = new
            {
                platform = "dotnet",
                platformVersion = Environment.Version.ToString(),
                os = Environment.OSVersion.ToString(),
            },
            auth = new
            {
                username = Username,
                jwtExpiryHours = JwtExpiry.TotalHours,
            },
            environment = new
            {
                QCLI_ADMIN_USERNAME = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QCLI_ADMIN_USERNAME")) ? "(set)" : "(default)",
                QCLI_ADMIN_PASSWORD = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QCLI_ADMIN_PASSWORD")) ? "(set)" : "(default)",
                QCLI_ADMIN_JWT_SECRET = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QCLI_ADMIN_JWT_SECRET")) ? "(set)" : "(auto-generated)",
            },
        };
    }
}
