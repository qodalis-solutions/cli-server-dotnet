using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.Audit.Extensions;
using Qodalis.Cli.Plugin.Users.Controllers;
using Qodalis.Cli.Plugin.Users.Middleware;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Users.Extensions;

/// <summary>
/// Extension methods to register the users plugin with <see cref="CliBuilder"/>.
/// </summary>
public static class CliBuilderUsersExtensions
{
    /// <summary>
    /// Registers users plugin services, stores, and controllers, and adds the audit plugin (<c>AddAudit</c>), where
    /// the plugin records sign-ins, token and group changes.
    /// Stores default to in-memory implementations; override them with the
    /// <c>Set*Store&lt;T&gt;()</c> options (DI-constructed), the store properties (instances),
    /// or by registering the store interface in the service collection before calling this.
    /// </summary>
    public static CliBuilder AddUsers(this CliBuilder builder, Action<UsersModuleOptions>? configure = null)
    {
        var options = new UsersModuleOptions();
        configure?.Invoke(options);

        var services = builder.Services;
        builder.AddInstalledPlugin(typeof(UsersModuleOptions).Assembly);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        RegisterStore<IUserStore, InMemoryUserStore>(services, options, options.UserStore);
        RegisterStore<ISessionStore, InMemorySessionStore>(services, options, options.SessionStore);
        RegisterStore<ICredentialStore, InMemoryCredentialStore>(services, options, options.CredentialStore);
        // The audit log is the audit plugin's: the users plugin records its events there and adds the plugin
        if (options.GetStoreFactory(typeof(IAuditStore), options.AuditStore) is { } auditStore)
        {
            services.RemoveAll<IAuditStore>();
            services.AddSingleton(sp => (IAuditStore)auditStore(sp));
        }
        builder.AddAudit();
        RegisterStore<IGroupStore, InMemoryGroupStore>(services, options, options.GroupStore);
        RegisterStore<IApiTokenStore, InMemoryApiTokenStore>(services, options, options.ApiTokenStore);

        // Guard-only: the users routes and the server protection belong to the service that owns the users. It never
        // runs next to CLI routes, which it would leave unprotected: startup fails unless WithoutCliRoutes() is called
        // (checked when the app starts, so the two calls may come in either order).
        if (options.GuardOnly)
        {
            options.ProtectServer = false;
            services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, GuardOnlyStartupCheck>();
        }
        else
        {
            // The dashboard's Users, Account (the users build, at its own prefix) and API tokens pages, when the
            // package carries their builds
            var assembly = typeof(UsersModuleOptions).Assembly;
            builder.AddDashboardModule(CliDashboardModules.Users, assembly);
            builder.AddDashboardModule(CliDashboardModules.Account, assembly);
            builder.AddDashboardModule(CliDashboardModules.Tokens, assembly);
        }
        // B1: protect the whole CLI server (fail closed in core even without the middleware).
        builder.ServerOptions.RequireAuthentication = options.ProtectServer;
        // The host application signs users in (admin dashboard: GET /api/v1/qcli/auth/mode)
        builder.ServerOptions.ExternalAuthentication = options.UsesExternalAuth;

        services.AddSingleton<AuthService>();
        services.AddSingleton<IApiTokenManager, ApiTokenManager>();
        services.AddSingleton<UsersService>();
        services.AddSingleton<IpRateLimiter>();
        services.AddSingleton<LinkService>();
        services.AddSingleton<ICliAuthBridge, UsersAuthBridge>();

        if (options.AuthAdapter != null)
            services.AddSingleton(options.AuthAdapter);
        else if (options.AuthAdapterType != null)
            services.AddSingleton(typeof(IExternalAuthAdapter), options.AuthAdapterType);

        // Seeds built-in groups always, and the root user unless external auth is used.
        services.AddSingleton<UsersBootstrapService>();
        services.AddHostedService(sp => sp.GetRequiredService<UsersBootstrapService>());

        // Once per builder: AddUsers may run twice, while a host entry with one of these names must still collide.
        builder.AddPermissionsOnce(typeof(UsersModuleOptions).FullName!, AddCatalogEntries, CliPermissionSurface.Cli);
        // The users config provider (sessions, lockout, passwords at runtime), once per builder
        if (services.All(d => d.ServiceType != typeof(UsersConfigProviderRegistered)))
        {
            services.AddSingleton<UsersConfigProviderRegistered>();
            builder.AddConfigProvider(sp => UsersConfigProvider.Create(sp.GetRequiredService<UsersModuleOptions>()));
        }
        if (!options.GuardOnly)
            builder.AddApplicationPart(typeof(SessionsController).Assembly);

        return builder;
    }

    /// <summary>The permissions the plugin's routes check, for the permission pickers (audit:* are core's).</summary>
    private static void AddCatalogEntries(CliPermissionCatalogBuilder p) => p
        .Group("Users")
        .Add(UsersPermissions.UsersRead, "See users and their permissions")
        .Add(UsersPermissions.UsersCreate, "Create users")
        .Add(UsersPermissions.UsersUpdate, "Change users, their groups and passwords")
        .Add(UsersPermissions.UsersDelete, "Delete users")
        .Group("Groups")
        .Add(UsersPermissions.GroupsRead, "See groups")
        .Add(UsersPermissions.GroupsCreate, "Create groups")
        .Add(UsersPermissions.GroupsUpdate, "Change groups and their permissions")
        .Add(UsersPermissions.GroupsDelete, "Delete groups")
        .Group("Sessions")
        .Add(UsersPermissions.SessionsRead, "See every user's sessions")
        .Add(UsersPermissions.SessionsRevoke, "End other users' sessions")
        .Group("Tokens")
        .Add(UsersPermissions.TokensRead, "See every user's API tokens")
        .Add(UsersPermissions.TokensRevoke, "Revoke other users' API tokens");

    internal const string GuardOnlyNeedsNoCliRoutesMessage = "guard-only needs WithoutCliRoutes()";

    /// <summary>Fails startup when guard-only mode is combined with CLI routes (they would be served unprotected).</summary>
    private sealed class GuardOnlyStartupCheck(CliServerOptions serverOptions) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            if (serverOptions.CliRoutes) throw new InvalidOperationException(GuardOnlyNeedsNoCliRoutesMessage);
            return next;
        }
    }

    private static void RegisterStore<TService, TDefault>(IServiceCollection services, UsersModuleOptions options, TService? instance)
        where TService : class
        where TDefault : class, TService
    {
        var factory = options.GetStoreFactory(typeof(TService), instance);
        if (factory != null)
        {
            services.RemoveAll<TService>();
            services.AddSingleton(sp => (TService)factory(sp));
        }
        else
        {
            services.TryAddSingleton<TService, TDefault>();
        }
    }

    /// <summary>
    /// Adds the users auth middleware to the pipeline. Call after UseCors(), before
    /// UseAuthorization() and before UseCli(). Without it, every users endpoint except login —
    /// and, with ProtectServer, every CLI route and socket — answers 401.
    /// Without CLI routes (<c>WithoutCliRoutes()</c>, which guard-only mode requires) there is nothing for it to
    /// protect and no middleware is added.
    /// </summary>
    public static IApplicationBuilder UseQodalisUsers(this IApplicationBuilder app)
    {
        if (app.ApplicationServices.GetService<CliServerOptions>()?.CliRoutes == false) return app;
        app.UseMiddleware<UsersAuthMiddleware>();
        return app;
    }
}

/// <summary>Marks the users config provider as registered (AddUsers may run twice).</summary>
internal sealed class UsersConfigProviderRegistered;
