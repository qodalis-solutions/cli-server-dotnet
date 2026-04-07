using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users;

/// <summary>
/// Configuration options for the users plugin.
/// </summary>
public class UsersModuleOptions
{
    /// <summary>
    /// Custom user store implementation. Defaults to in-memory.
    /// </summary>
    public IUserStore? UserStore { get; set; }

    /// <summary>
    /// Custom session store implementation. Defaults to in-memory.
    /// </summary>
    public ISessionStore? SessionStore { get; set; }

    /// <summary>
    /// Custom credential store implementation. Defaults to in-memory.
    /// </summary>
    public ICredentialStore? CredentialStore { get; set; }

    /// <summary>
    /// Custom audit store implementation. Defaults to in-memory.
    /// </summary>
    public IAuditStore? AuditStore { get; set; }

    /// <summary>
    /// Custom group store implementation. Defaults to in-memory.
    /// </summary>
    public IGroupStore? GroupStore { get; set; }

    /// <summary>
    /// Session timeout in milliseconds. Defaults to 30 minutes.
    /// </summary>
    public long? SessionTimeoutMs { get; set; }

    /// <summary>
    /// Default admin password used when seeding the initial admin user. Defaults to "root".
    /// </summary>
    public string? DefaultAdminPassword { get; set; }
}
