using Qodalis.Cli.Security;

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

    private readonly CliServerOptions _serverOptions;

    /// <summary>Initializes a new instance of the <see cref="AdminAuthMode"/> class.</summary>
    public AdminAuthMode(CliServerOptions serverOptions)
    {
        _serverOptions = serverOptions;
    }

    /// <summary>
    /// True when the host application signs users in (<see cref="CliServerOptions.ExternalAuthentication"/>,
    /// set by the users plugin's <c>UseExternalAuth</c>).
    /// </summary>
    public bool IsExternal => _serverOptions.ExternalAuthentication;

    /// <summary><see cref="External"/> or <see cref="Password"/>.</summary>
    public string Mode => IsExternal ? External : Password;
}
