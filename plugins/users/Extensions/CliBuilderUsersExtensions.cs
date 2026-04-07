using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Users.Controllers;
using Qodalis.Cli.Plugin.Users.Middleware;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;

namespace Qodalis.Cli.Plugin.Users.Extensions;

/// <summary>
/// Extension methods to register the users plugin with <see cref="CliBuilder"/>.
/// </summary>
public static class CliBuilderUsersExtensions
{
    /// <summary>
    /// Registers users plugin services, stores, and controllers.
    /// </summary>
    public static CliBuilder AddUsers(this CliBuilder builder, Action<UsersModuleOptions>? configure = null)
    {
        var options = new UsersModuleOptions();
        configure?.Invoke(options);

        // Register options
        builder.Services.AddSingleton(options);

        // Register stores (use custom overrides or in-memory defaults)
        builder.Services.AddSingleton<IUserStore>(options.UserStore ?? new InMemoryUserStore());
        builder.Services.AddSingleton<ISessionStore>(options.SessionStore ?? new InMemorySessionStore());
        builder.Services.AddSingleton<ICredentialStore>(options.CredentialStore ?? new InMemoryCredentialStore());
        builder.Services.AddSingleton<IAuditStore>(options.AuditStore ?? new InMemoryAuditStore());
        builder.Services.AddSingleton<IGroupStore>(options.GroupStore ?? new InMemoryGroupStore());

        // Register services
        builder.Services.AddSingleton<AuthService>();

        // Register hosted service for bootstrapping and session cleanup
        builder.Services.AddHostedService<UsersBootstrapService>();

        // Register controllers from this assembly
        builder.AddApplicationPart(typeof(UsersAuthController).Assembly);

        return builder;
    }

    /// <summary>
    /// Adds the users auth middleware to the pipeline.
    /// Call after UseCors() and before UseAuthorization().
    /// </summary>
    public static IApplicationBuilder UseQodalisUsers(this IApplicationBuilder app)
    {
        app.UseMiddleware<UsersAuthMiddleware>();
        return app;
    }
}
