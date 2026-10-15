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

    /// <summary>Idle session timeout in milliseconds. Defaults to 30 minutes.</summary>
    public long? SessionTimeoutMs { get; set; }

    /// <summary>
    /// Password for the seeded <c>root</c> user. When not set, a random password is generated,
    /// logged once with a warning, and root must change it on first login.
    /// </summary>
    public string? DefaultAdminPassword { get; set; }

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

    /// <summary>Effective idle session timeout.</summary>
    public long EffectiveSessionTimeoutMs => SessionTimeoutMs is > 0 ? SessionTimeoutMs.Value : DefaultSessionTimeoutMs;

    /// <summary>Effective absolute session lifetime.</summary>
    public long EffectiveMaxSessionLifetimeMs => MaxSessionLifetimeMs > 0 ? MaxSessionLifetimeMs : DefaultMaxSessionLifetimeMs;

    /// <summary>True when an external auth adapter (instance or type) is configured.</summary>
    public bool UsesExternalAuth => AuthAdapter != null || AuthAdapterType != null;

    /// <summary>
    /// Convenience method to set a delegate-based external auth adapter.
    /// When an adapter is configured, the plugin's login/logout/verify/password
    /// endpoints return 501 and no <c>root</c> user is seeded (groups still are).
    /// </summary>
    public void UseExternalAuth(Func<HttpContext, ExternalAuthResult?> adapter)
    {
        AuthAdapter = new DelegateAuthAdapter(adapter);
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
