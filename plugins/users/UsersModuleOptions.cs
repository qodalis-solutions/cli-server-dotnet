using Qodalis.Cli.Abstractions.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users;

/// <summary>
/// Configuration options for the users plugin.
/// </summary>
public class UsersModuleOptions
{
    /// <summary>Default idle session timeout (30 minutes).</summary>
    public const long DefaultSessionTimeoutMs = 30 * 60 * 1000;

    /// <summary>Default lockout duration (5 minutes).</summary>
    public const long DefaultLockoutMs = 5 * 60 * 1000;

    private readonly Dictionary<Type, Func<IServiceProvider, object>> _storeFactories = new();

    /// <summary>
    /// Custom user store instance. Prefer <see cref="SetUserStore{T}"/> for DI-constructed stores.
    /// </summary>
    public IUserStore? UserStore { get; set; }

    /// <summary>Custom session store instance. Prefer <see cref="SetSessionStore{T}"/>.</summary>
    public ISessionStore? SessionStore { get; set; }

    /// <summary>Custom credential store instance. Prefer <see cref="SetCredentialStore{T}"/>.</summary>
    public ICredentialStore? CredentialStore { get; set; }

    /// <summary>Custom audit store instance. Prefer <see cref="SetAuditStore{T}"/>.</summary>
    public IAuditStore? AuditStore { get; set; }

    /// <summary>Custom group store instance. Prefer <see cref="SetGroupStore{T}"/>.</summary>
    public IGroupStore? GroupStore { get; set; }

    /// <summary>Custom API token store instance. Prefer <see cref="SetApiTokenStore{T}"/>.</summary>
    public IApiTokenStore? ApiTokenStore { get; set; }

    /// <summary>
    /// Allows API tokens that never expire (<c>expiresInDays: null</c>). Defaults to <c>false</c>
    /// (tokens expire after 1-365 days, 90 by default).
    /// </summary>
    public bool AllowNonExpiringTokens { get; set; }

    /// <summary>
    /// With an external auth adapter, also accept the plugin's API tokens (<c>qcli_pat_…</c> bearer tokens, checked
    /// before the adapter), so users can create tokens (<c>POST /tokens</c>) and approve terminal links (<c>server add</c>).
    /// Off by default: the host owns sign-in. The token's user is read from the user store by id on every request
    /// and socket re-check, so the store must find the adapter's users and report <see cref="Models.User.Disabled"/>;
    /// keep the tokens in a persistent <see cref="IApiTokenStore"/> (the default store forgets them on restart).
    /// Ignored without an adapter (tokens are always on).
    /// </summary>
    public bool ApiTokensWithExternalAuth { get; set; }

    /// <summary>
    /// The page where users approve a terminal's link request (<c>server add</c> in the web CLI): an http(s) URL
    /// or a root-relative path; <c>{code}</c> in it is replaced by the code, otherwise <c>?code=</c> is appended.
    /// <c>null</c> = the admin dashboard's <c>/qcli/admin/link</c>. Throws <see cref="ArgumentException"/> for
    /// anything else (e.g. <c>javascript:</c> or <c>//host</c>).
    /// </summary>
    public string? LinkApprovalUrl
    {
        get => _linkApprovalUrl;
        set
        {
            if (value != null && !Services.LinkService.IsValidApprovalUrl(value))
                throw new ArgumentException("[users] LinkApprovalUrl must be an http(s) URL or a root-relative path", nameof(value));
            _linkApprovalUrl = value;
        }
    }

    private string? _linkApprovalUrl;

    /// <summary>Idle session timeout in milliseconds. Defaults to 30 minutes.</summary>
    public long? SessionTimeoutMs { get; set; }

    /// <summary>
    /// Password for the seeded <c>root</c> user. When not set, a random password is generated,
    /// printed once to the process's standard error (never through <c>ILogger</c>, whose output the
    /// log WebSocket and the admin log buffer expose), and root must change it on first login.
    /// </summary>
    public string? DefaultAdminPassword { get; set; }

    /// <summary>
    /// Where the generated root password is printed; <c>null</c> = <see cref="Console.Error"/>. Test seam.
    /// </summary>
    internal TextWriter? SecretOutput { get; set; }

    /// <summary>Default absolute session lifetime (12 hours).</summary>
    public const long DefaultMaxSessionLifetimeMs = 12 * 60 * 60 * 1000;

    /// <summary>
    /// Protect the whole CLI server (commands, files, data explorer, jobs, sockets), not only the
    /// users routes. Defaults to <c>true</c>.
    /// </summary>
    public bool ProtectServer { get; set; } = true;

    /// <summary>Absolute session lifetime in milliseconds (in addition to the idle timeout). Defaults to 12 hours.</summary>
    public long MaxSessionLifetimeMs { get; set; } = DefaultMaxSessionLifetimeMs;

    /// <summary>Minimum password length. Defaults to 8.</summary>
    public int MinPasswordLength { get; set; } = 8;

    /// <summary>Maximum password length. Defaults to 1024.</summary>
    public int MaxPasswordLength { get; set; } = 1024;

    /// <summary>
    /// Attempts per minute per client IP on login, verify-password and self password change. Defaults to 20.
    /// </summary>
    public int MaxAttemptsPerIpPerMinute { get; set; } = 20;

    /// <summary>
    /// Trust <c>X-Forwarded-For</c> for the client IP (only behind a trusted reverse proxy). When on, the
    /// client IP is the entry <see cref="TrustedProxyHops"/> positions from the right of the header (the one
    /// appended by the outermost trusted proxy); when off, the header is ignored and the connection's remote
    /// address is used. Defaults to false.
    /// </summary>
    public bool TrustProxy { get; set; }

    /// <summary>
    /// Number of trusted reverse proxies in front of the server, used when <see cref="TrustProxy"/> is on.
    /// The client IP is the <c>X-Forwarded-For</c> entry at index <c>count - TrustedProxyHops</c> (with 1, the
    /// rightmost entry); the leftmost entry when the header has fewer entries, and the remote address when it
    /// is absent or empty. Defaults to 1.
    /// </summary>
    public int TrustedProxyHops { get; set; } = 1;

    /// <summary>Maximum concurrent PBKDF2 computations. Defaults to 4.</summary>
    public int PasswordHashConcurrency { get; set; } = 4;

    /// <summary>Consecutive failed logins before an account is locked. Defaults to 5.</summary>
    public int MaxLoginAttempts { get; set; } = 5;

    /// <summary>Lockout duration in milliseconds. Defaults to 5 minutes.</summary>
    public long LockoutMs { get; set; } = DefaultLockoutMs;

    /// <summary>
    /// Optional external auth adapter. When set, the plugin delegates authentication
    /// to the host application instead of using its own Bearer token scheme.
    /// </summary>
    public IExternalAuthAdapter? AuthAdapter { get; set; }

    internal Type? AuthAdapterType { get; private set; }

    /// <summary>
    /// The host application owns the users (e.g. a read-only <see cref="IUserStore"/> over its own accounts):
    /// creating, updating and deleting users, password changes and group membership changes answer
    /// <c>501</c> without calling the stores, no <c>root</c> user is seeded, and
    /// <c>GET /sessions/current</c> reports <c>usersReadOnly: true</c> so clients hide those commands.
    /// </summary>
    public bool UsersReadOnly { get; set; }

    /// <summary>
    /// The host application owns the groups: creating, updating and deleting groups answer <c>501</c>
    /// without calling the store, the built-in groups are not seeded, and <c>GET /sessions/current</c>
    /// reports <c>groupsReadOnly: true</c>.
    /// </summary>
    public bool GroupsReadOnly { get; set; }

    /// <summary>Sets <see cref="UsersReadOnly"/>.</summary>
    public UsersModuleOptions ReadOnlyUsers(bool readOnly = true)
    {
        UsersReadOnly = readOnly;
        return this;
    }

    /// <summary>Sets <see cref="GroupsReadOnly"/>.</summary>
    public UsersModuleOptions ReadOnlyGroups(bool readOnly = true)
    {
        GroupsReadOnly = readOnly;
        return this;
    }

    /// <summary>Effective idle session timeout.</summary>
    public long EffectiveSessionTimeoutMs => SessionTimeoutMs is > 0 ? SessionTimeoutMs.Value : DefaultSessionTimeoutMs;

    /// <summary>Effective absolute session lifetime.</summary>
    public long EffectiveMaxSessionLifetimeMs => MaxSessionLifetimeMs > 0 ? MaxSessionLifetimeMs : DefaultMaxSessionLifetimeMs;

    /// <summary>True when an external auth adapter (instance or type) is configured.</summary>
    public bool UsesExternalAuth => AuthAdapter != null || AuthAdapterType != null;

    /// <summary>True when API tokens are accepted: always without an adapter, with one only with <see cref="ApiTokensWithExternalAuth"/>.</summary>
    public bool ApiTokensEnabled => !UsesExternalAuth || ApiTokensWithExternalAuth;

    /// <summary>Turns on <see cref="ApiTokensWithExternalAuth"/>: API tokens and terminal links next to the host's sign-in.</summary>
    public UsersModuleOptions AllowApiTokens()
    {
        ApiTokensWithExternalAuth = true;
        return this;
    }

    /// <summary>
    /// Convenience method to set a delegate-based external auth adapter.
    /// When an adapter is configured, the plugin's login/logout/verify/password
    /// endpoints return 501 and no <c>root</c> user is seeded (groups still are).
    /// </summary>
    public void UseExternalAuth(Func<HttpContext, ExternalAuthResult?> adapter)
    {
        AuthAdapter = new DelegateAuthAdapter(adapter);
    }

    /// <summary>
    /// Sets an asynchronous delegate-based external auth adapter, for hosts that load the user,
    /// their roles or their state from a database. For requests and socket upgrades the token is the
    /// request's <see cref="HttpContext.RequestAborted"/>; when an open socket is re-checked on its
    /// rebuilt request, it is cancelled when the server stops.
    /// </summary>
    public UsersModuleOptions UseExternalAuth(Func<HttpContext, CancellationToken, ValueTask<ExternalAuthResult?>> adapter)
    {
        AuthAdapter = new DelegateAuthAdapter(adapter);
        return this;
    }

    /// <summary>Uses <paramref name="adapter"/> as the external auth adapter.</summary>
    public UsersModuleOptions UseExternalAuth(IExternalAuthAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        AuthAdapter = adapter;
        return this;
    }

    /// <summary>Uses an external auth adapter constructed through dependency injection.</summary>
    public UsersModuleOptions UseExternalAuth<T>() where T : class, IExternalAuthAdapter
    {
        AuthAdapterType = typeof(T);
        return this;
    }

    /// <summary>Registers <typeparamref name="T"/> (constructed via DI, singleton) as the user store.</summary>
    public UsersModuleOptions SetUserStore<T>() where T : class, IUserStore => SetStore<IUserStore, T>();

    /// <summary>Registers a factory for the user store.</summary>
    public UsersModuleOptions SetUserStore(Func<IServiceProvider, IUserStore> factory) => SetStore(factory);

    /// <summary>Registers <typeparamref name="T"/> (constructed via DI, singleton) as the session store.</summary>
    public UsersModuleOptions SetSessionStore<T>() where T : class, ISessionStore => SetStore<ISessionStore, T>();

    /// <summary>Registers a factory for the session store.</summary>
    public UsersModuleOptions SetSessionStore(Func<IServiceProvider, ISessionStore> factory) => SetStore(factory);

    /// <summary>Registers <typeparamref name="T"/> (constructed via DI, singleton) as the credential store.</summary>
    public UsersModuleOptions SetCredentialStore<T>() where T : class, ICredentialStore => SetStore<ICredentialStore, T>();

    /// <summary>Registers a factory for the credential store.</summary>
    public UsersModuleOptions SetCredentialStore(Func<IServiceProvider, ICredentialStore> factory) => SetStore(factory);

    /// <summary>Registers <typeparamref name="T"/> (constructed via DI, singleton) as the audit store.</summary>
    public UsersModuleOptions SetAuditStore<T>() where T : class, IAuditStore => SetStore<IAuditStore, T>();

    /// <summary>Registers a factory for the audit store.</summary>
    public UsersModuleOptions SetAuditStore(Func<IServiceProvider, IAuditStore> factory) => SetStore(factory);

    /// <summary>Registers <typeparamref name="T"/> (constructed via DI, singleton) as the group store.</summary>
    public UsersModuleOptions SetGroupStore<T>() where T : class, IGroupStore => SetStore<IGroupStore, T>();

    /// <summary>Registers a factory for the group store.</summary>
    public UsersModuleOptions SetGroupStore(Func<IServiceProvider, IGroupStore> factory) => SetStore(factory);

    /// <summary>Registers <typeparamref name="T"/> (constructed via DI, singleton) as the API token store.</summary>
    public UsersModuleOptions SetApiTokenStore<T>() where T : class, IApiTokenStore => SetStore<IApiTokenStore, T>();

    /// <summary>Registers a factory for the API token store.</summary>
    public UsersModuleOptions SetApiTokenStore(Func<IServiceProvider, IApiTokenStore> factory) => SetStore(factory);

    private UsersModuleOptions SetStore<TService, TImpl>() where TService : class where TImpl : class, TService
    {
        _storeFactories[typeof(TService)] = sp => ActivatorUtilities.GetServiceOrCreateInstance<TImpl>(sp);
        return this;
    }

    private UsersModuleOptions SetStore<TService>(Func<IServiceProvider, TService> factory) where TService : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        _storeFactories[typeof(TService)] = factory;
        return this;
    }

    /// <summary>
    /// Returns the registration for a store: the explicitly assigned instance, then the
    /// <c>Set*Store</c> factory, or <c>null</c> to use the in-memory default.
    /// </summary>
    internal Func<IServiceProvider, object>? GetStoreFactory(Type serviceType, object? instance)
    {
        if (instance != null) return _ => instance;
        return _storeFactories.TryGetValue(serviceType, out var factory) ? factory : null;
    }
}
