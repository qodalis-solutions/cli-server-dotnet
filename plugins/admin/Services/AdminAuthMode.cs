namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>
/// How the dashboard signs users in: <c>password</c> (the dashboard's own form, backed by the
/// users plugin or the static admin credentials) or <c>external</c> (the users plugin delegates
/// authentication to the host application through an external auth adapter).
/// </summary>
public sealed class AdminAuthMode
{
    /// <summary>Mode name for the dashboard's own username/password form.</summary>
    public const string Password = "password";

    /// <summary>Mode name for sign-in through the host application.</summary>
    public const string External = "external";

    // The admin plugin does not reference the users plugin, so its options are read by name.
    private const string UsersOptionsTypeName = "Qodalis.Cli.Plugin.Users.UsersModuleOptions";
    private const string UsesExternalAuthProperty = "UsesExternalAuth";

    private readonly Lazy<bool> _isExternal;

    /// <summary>Initializes a new instance of the <see cref="AdminAuthMode"/> class.</summary>
    public AdminAuthMode(IServiceProvider services)
    {
        _isExternal = new Lazy<bool>(() => DetectExternal(services));
    }

    /// <summary>True when the users plugin is configured with an external auth adapter.</summary>
    public bool IsExternal => _isExternal.Value;

    /// <summary><see cref="External"/> or <see cref="Password"/>.</summary>
    public string Mode => IsExternal ? External : Password;

    private static bool DetectExternal(IServiceProvider services)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(UsersOptionsTypeName, throwOnError: false);
            if (type == null) continue;

            var options = services.GetService(type);
            if (options == null) return false;

            return type.GetProperty(UsesExternalAuthProperty)?.GetValue(options) is true;
        }

        return false;
    }
}
