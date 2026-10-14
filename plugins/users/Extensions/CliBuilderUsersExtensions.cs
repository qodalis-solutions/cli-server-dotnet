using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qodalis.Cli.Extensions;
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
    /// Registers users plugin services, stores, and controllers.
    /// Stores default to in-memory implementations; override them with the
    /// <c>Set*Store&lt;T&gt;()</c> options (DI-constructed), the store properties (instances),
    /// or by registering the store interface in the service collection before calling this.
    /// </summary>
    public static CliBuilder AddUsers(this CliBuilder builder, Action<UsersModuleOptions>? configure = null)
    {
        var options = new UsersModuleOptions();
        configure?.Invoke(options);

        var services = builder.Services;
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        RegisterStore<IUserStore, InMemoryUserStore>(services, options, options.UserStore);
        RegisterStore<ISessionStore, InMemorySessionStore>(services, options, options.SessionStore);
        RegisterStore<ICredentialStore, InMemoryCredentialStore>(services, options, options.CredentialStore);
        RegisterStore<IAuditStore, InMemoryAuditStore>(services, options, options.AuditStore);
        RegisterStore<IGroupStore, InMemoryGroupStore>(services, options, options.GroupStore);

        // B1: protect the whole CLI server (fail closed in core even without the middleware).
        builder.ServerOptions.RequireAuthentication = options.ProtectServer;

        services.AddSingleton<AuthService>();
        services.AddSingleton<UsersService>();
        services.AddSingleton<IpRateLimiter>();
        services.AddSingleton<ICliAuthBridge, UsersAuthBridge>();

        if (options.AuthAdapter != null)
            services.AddSingleton(options.AuthAdapter);
        else if (options.AuthAdapterType != null)
            services.AddSingleton(typeof(IExternalAuthAdapter), options.AuthAdapterType);

        // Seeds built-in groups always, and the root user unless external auth is used.
        services.AddSingleton<UsersBootstrapService>();
        services.AddHostedService(sp => sp.GetRequiredService<UsersBootstrapService>());

        builder.AddApplicationPart(typeof(SessionsController).Assembly);

        return builder;
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
    /// </summary>
    public static IApplicationBuilder UseQodalisUsers(this IApplicationBuilder app)
    {
        app.UseMiddleware<UsersAuthMiddleware>();
        return app;
    }
}
