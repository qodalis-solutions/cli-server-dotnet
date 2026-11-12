using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>
/// Admin configuration — reads credentials and JWT settings from environment variables.
/// </summary>
public class AdminConfig
{
    private readonly ILogger<AdminConfig> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminConfig"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public AdminConfig(ILogger<AdminConfig> logger)
    {
        _logger = logger;
    }

    /// <summary>Admin username for login.</summary>
    public string Username { get; set; } = "admin";
    /// <summary>Admin password for login.</summary>
    public string Password { get; set; } = "admin";
    /// <summary>Secret key used for signing JWT tokens.</summary>
    public string JwtSecret { get; set; } = string.Empty;
    /// <summary>Duration before JWT tokens expire.</summary>
    public TimeSpan JwtExpiry { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Optional explicit path to the dashboard dist directory.</summary>
    public string? DashboardPath { get; set; }

    /// <summary>
    /// Color scheme of the dashboard: the mode and per-scheme color overrides.
    /// Served at <c>/qcli/admin/theme.json</c>; values are validated at startup.
    /// </summary>
    public AdminTheme Theme { get; set; } = new();

    /// <summary>
    /// The dashboard's name and icon and its terminal's branding, for applications that ship the dashboard under
    /// their own name. Served with the color scheme at <c>/qcli/admin/theme.json</c>; values are validated at
    /// startup. Unset: Qodalis Cli Admin with the Qodalis Solutions logo.
    /// </summary>
    public AdminBranding Branding { get; set; } = new();

    /// <summary>
    /// Where the host application signs users in, shown by the dashboard in external sign-in mode
    /// (users plugin with an external auth adapter) and returned by <c>GET /api/v1/qcli/auth/mode</c>.
    /// An absolute <c>http(s)</c> URL or a root-relative path (<c>/sign-in</c>). Null: the dashboard
    /// tells users to sign in through the host application.
    /// </summary>
    /// <remarks>
    /// The dashboard sends users back: it fills <c>{returnTo}</c> in the URL, or appends <c>returnTo=</c>, with the
    /// dashboard page they were on (e.g. a terminal link's approval page); after signing in, the host opens that URL
    /// with <c>#token=&lt;bearer token&gt;</c>.
    /// </remarks>
    public string? SignInUrl { get; set; }

    /// <summary>
    /// Validates the options set by the host. Throws <see cref="ArgumentException"/> on an invalid
    /// <see cref="SignInUrl"/>.
    /// </summary>
    internal void Validate()
    {
        if (SignInUrl != null && !IsValidSignInUrl(SignInUrl))
        {
            throw new ArgumentException(
                $"AdminConfig.SignInUrl must be an absolute http(s) URL or a root-relative path, got '{SignInUrl}'.",
                nameof(SignInUrl));
        }
    }

    /// <summary>
    /// True for an absolute <c>http</c>/<c>https</c> URL with a host, or a root-relative path
    /// (<c>/sign-in</c>; not protocol-relative <c>//host</c>). Rejects other schemes such as
    /// <c>javascript:</c>, and control characters or whitespace.
    /// </summary>
    public static bool IsValidSignInUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            return false;

        if (value.StartsWith('/'))
            return value.Length == 1 || (value[1] != '/' && value[1] != '\\');

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host);
    }

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
            _logger.LogWarning("Using default admin credentials. Set QCLI_ADMIN_USERNAME and QCLI_ADMIN_PASSWORD environment variables.");
        }
    }

    /// <summary>
    /// Validates the provided credentials against the configured admin username and password using constant-time comparison.
    /// </summary>
    /// <param name="username">The username to validate.</param>
    /// <param name="password">The password to validate.</param>
    /// <returns>True if both username and password match.</returns>
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

    /// <summary>
    /// Returns the configuration sections for the admin config endpoint.
    /// </summary>
    /// <returns>An array of configuration section objects.</returns>
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
