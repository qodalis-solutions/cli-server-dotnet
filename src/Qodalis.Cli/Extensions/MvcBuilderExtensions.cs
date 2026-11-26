using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Controllers;
using Qodalis.Cli.FileSystem;
using Qodalis.Cli.Logging;
using Qodalis.Cli.Mcp;
using Qodalis.Cli.Restart;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Plugins;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Extensions;

/// <summary>
/// Extension methods for <see cref="IMvcBuilder"/> to register CLI services and controllers.
/// </summary>
public static class MvcBuilderExtensions
{
    /// <summary>
    /// Registers CLI controllers, services, and command processors with the MVC pipeline.
    /// </summary>
    /// <param name="builder">The MVC builder.</param>
    /// <param name="configure">Optional action to configure CLI processors, modules, and features.</param>
    /// <returns>The MVC builder for chaining.</returns>
    public static IMvcBuilder AddCli(this IMvcBuilder builder, Action<CliBuilder>? configure = null)
    {
        var cliBuilder = new CliBuilder(builder.Services);
        configure?.Invoke(cliBuilder);

        // The plugins added in code (cli.AddPlugin) and those installed at runtime from the marketplace, in their declared
        // order after the host's own registrations (a failing one is left out and its error kept; the server starts all the same)
        var serverPlugins = cliBuilder.ServerPlugins;
        foreach (var (entry, id, options) in cliBuilder.CodePlugins) serverPlugins.AddCode(entry, id, options);
        serverPlugins.Load(cliBuilder);
        builder.Services.AddSingleton(serverPlugins);
        // The data directory (installed plugins and their file manifests, the plugins' settings) is never served by the
        // filesystem API, even below an allowed path
        if (cliBuilder.FileSystemApiOptions is { } fileSystem && !fileSystem.DeniedPaths.Contains(serverPlugins.DataDir))
            fileSystem.DeniedPaths.Add(serverPlugins.DataDir);
        builder.Services.AddHostedService(sp => new CliServerPluginsStartup(serverPlugins, sp));

        // Without CLI routes neither core's controllers nor any plugin's are added, and those MVC discovers on its own
        // (a Web SDK host lists every referenced MVC assembly as an application part) are removed.
        if (!cliBuilder.ServerOptions.CliRoutes)
        {
            builder.PartManager.FeatureProviders.Add(new CliControllerFilter(cliBuilder.AdditionalAssemblyParts));
        }
        else
        {
            builder.PartManager.ApplicationParts
                .Add(new AssemblyPart(typeof(CliController).Assembly));

            // Add any additional assembly parts registered by plugins
            foreach (var assembly in cliBuilder.AdditionalAssemblyParts)
            {
                builder.PartManager.ApplicationParts
                    .Add(new AssemblyPart(assembly));
            }
        }

        builder.Services.AddSingleton<ICliCommandRegistry>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<CliCommandRegistry>>();
            var registry = new CliCommandRegistry(logger);
            var processors = sp.GetServices<ICliCommandProcessor>();
            foreach (var processor in processors)
            {
                registry.Register(processor);
            }
            // The config command (unless the host registered its own)
            if (cliBuilder.ServerOptions.CliRoutes && cliBuilder.ConfigOptions.Command && registry.FindProcessor("config") == null)
            {
                registry.Register(new CliConfigCommandProcessor(sp.GetRequiredService<ICliConfigService>(),
                    () => CliConfigContexts.Of(sp.GetService<IHttpContextAccessor>()?.HttpContext, cliBuilder.ServerOptions, "terminal")));
            }
            return registry;
        });

        AddConfigServices(builder.Services, cliBuilder);
        AddRestartServices(builder.Services, cliBuilder);

        builder.Services.AddSingleton(new CliInstalledPlugins(cliBuilder.InstalledPlugins));
        // The runtime registry: the builder's extensions when first resolved, then pages added and removed at runtime
        builder.Services.AddSingleton(_ => new CliDashboardExtensions(cliBuilder.DashboardExtensions));

        var serverOptions = cliBuilder.ServerOptions;
        builder.Services.AddSingleton(serverOptions);
        builder.Services.Configure<MvcOptions>(o => o.Filters.Add(new CliAuthenticationGuardFilter()));
        // The default CORS policy follows the live list of allowed origins (the cors config provider changes it at
        // runtime): an empty list sends no CORS headers. Applied where the host calls app.UseCors().
        builder.Services.AddCors(o => o.AddDefaultPolicy(policy => policy
            .SetIsOriginAllowed(serverOptions.IsOriginAllowed)
            .AllowAnyMethod()
            .AllowAnyHeader()));

        builder.Services.TryAddSingleton<CliCommandUsage>();
        builder.Services.AddSingleton<ICliCommandExecutorService, CliCommandExecutorService>();
        builder.Services.AddSingleton<ICliServerInfoService, CliServerInfoService>();
        builder.Services.AddSingleton<CliMcpServer>();
        // One event bus per server; the events WebSocket subscribes to it (a host may register its own bus first)
        builder.Services.TryAddSingleton<ICliEventBus, CliEventBus>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<ICliEventSocketManager, CliEventSocketManager>();
        builder.Services.AddSingleton<ICliLogSocketManager, CliLogSocketManager>();
        builder.Services.AddSingleton<ILoggerProvider, WebSocketLoggerProvider>();
        builder.Services.AddSingleton<CliShellSessionRegistry>();
        builder.Services.AddSingleton<ICliShellSessions>(sp => sp.GetRequiredService<CliShellSessionRegistry>());
        builder.Services.AddSingleton<IShellSessionManager, ShellSessionManager>();

        // Register IFileStorageProvider as singleton, publishing fs:changed after every change (API, MCP tools, ...)
        var fileStorage = cliBuilder.FileStorageProvider ?? new InMemoryFileStorageProvider();
        builder.Services.AddSingleton<IFileStorageProvider>(sp => new CliEventFileStorageProvider(
            fileStorage, sp.GetRequiredService<ICliEventBus>(), sp.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()));

        // Register DataExplorer services (always register so the controller can resolve)
        builder.Services.AddSingleton<DataExplorerRegistry>(sp =>
        {
            var registry = new DataExplorerRegistry();
            var registrations = sp.GetServices<DataExplorerProviderRegistration>().ToList();
            var policies = sp.GetServices<DataExplorerSourcePolicy>().ToList();

            for (var index = 0; index < registrations.Count; index++)
            {
                var registration = registrations[index];
                IDataExplorerProvider provider;
                var options = registration.Options;
                if (registration.Deferred != null)
                {
                    // A plugin's source: built from its settings now, left out while they are missing
                    if (registration.Deferred() is not { } built) continue;
                    (provider, options) = built;
                }
                else if (registration.ProviderInstance != null)
                {
                    provider = registration.ProviderInstance;
                }
                else if (registration.ProviderType != null)
                {
                    provider = (IDataExplorerProvider)sp.GetRequiredService(registration.ProviderType);
                }
                else
                {
                    continue;
                }

                foreach (var policy in policies.Where(p => p.After <= index)) policy.Apply(options);
                registry.Register(provider, options);
            }

            return registry;
        });
        builder.Services.AddSingleton<IDataExplorerRegistry>(sp => sp.GetRequiredService<DataExplorerRegistry>());

        builder.Services.AddSingleton<IDataExplorerExecutorService, DataExplorerExecutorService>();

        return builder;
    }

    /// <summary>Config providers: the default store and the stores per provider, the registry with the built-in providers, the service, its startup and MCP tools.</summary>
    private static void AddConfigServices(IServiceCollection services, CliBuilder cliBuilder)
    {
        var serverOptions = cliBuilder.ServerOptions;
        services.TryAddSingleton<ICliConfigStore, InMemoryConfigStore>();
        services.AddSingleton(sp =>
        {
            var registry = new CliConfigRegistry(sp.GetRequiredService<ICliConfigStore>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger("Qodalis.Cli.Config"));
            // The settings of plugins (code or marketplace) without a store of their own: in <data dir>/plugin-settings.json
            // while the server keeps its settings in memory, so they survive a restart; the host's stores still win
            if (registry.DefaultStore.GetType() == typeof(InMemoryConfigStore))
                foreach (var id in cliBuilder.ServerPlugins.StorelessSettings())
                    registry.SetStore(cliBuilder.ServerPlugins.PluginSettingsStore, id);
            // The host's stores per provider id: the options' map, then SetConfigStore(store, id) (the last one wins)
            foreach (var (providerId, store) in cliBuilder.ConfigOptions.Stores)
                registry.SetStore(store, providerId);
            foreach (var registration in sp.GetServices<CliConfigStoreRegistration>())
                registry.SetStore(registration.Factory(sp), registration.ProviderId);
            foreach (var provider in sp.GetServices<ICliConfigProvider>())
                registry.Add(provider);

            // The built-in providers (a provider the host registered under the same id wins)
            void BuiltIn(ICliConfigProvider provider)
            {
                if (!registry.Has(provider.Id)) registry.Add(provider);
            }
            BuiltIn(new CliServerConfigProvider(environment: () => sp.GetService<IHostEnvironment>()?.EnvironmentName));
            BuiltIn(new CliHostConfigProvider(() => HostOptions(serverOptions), sp.GetService<IConfiguration>(),
                cliBuilder.ConfigOptions.Environment));
            BuiltIn(CliRuntimeConfigProviders.Cors(serverOptions.AllowedOrigins, CliCorsMode.Origins));
            BuiltIn(CliRuntimeConfigProviders.Mcp(serverOptions.Mcp, CliRoutePolicy.McpPath));
            if (serverOptions.CliRoutes && sp.GetService<FileSystemOptions>() is { } fileSystem)
            {
                var storage = fileSystem.Provider?.Name ?? cliBuilder.FileStorageProvider?.Name ?? "os";
                BuiltIn(CliRuntimeConfigProviders.FileSystem(storage, fileSystem.AllowedPaths));
            }
            BuiltIn(CliCustomConfigProvider.Create());
            registry.Start();
            return registry;
        });
        services.AddSingleton(sp => new CliConfigService(sp.GetRequiredService<CliConfigRegistry>(), sp,
            sp.GetService<ILogger<CliConfigService>>()));
        services.AddSingleton<ICliConfigService>(sp => sp.GetRequiredService<CliConfigService>());
        services.AddHostedService<CliConfigStartup>();
        services.AddMcpToolProvider<ConfigMcpToolProvider>();
    }

    /// <summary>Restart: the service (its runtime the options', else one in the container, else the real process) and its startup.</summary>
    private static void AddRestartServices(IServiceCollection services, CliBuilder cliBuilder)
    {
        var options = cliBuilder.RestartOptions;
        options.Validate();
        services.AddSingleton(sp => new CliServerRestart(options,
            options.Runtime ?? sp.GetService<ICliServerRestartRuntime>() ?? new CliServerProcessRuntime(
                sp.GetService<IHostApplicationLifetime>(),
                sp.GetService<Microsoft.Extensions.Options.IOptions<HostOptions>>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger("Qodalis.Cli.Restart")),
            sp, sp.GetService<ILoggerFactory>()?.CreateLogger("Qodalis.Cli.Restart"), sp.GetService<TimeProvider>()));
        services.AddSingleton<Qodalis.Cli.Abstractions.Restart.ICliServerRestart>(sp => sp.GetRequiredService<CliServerRestart>());
        services.AddHostedService<CliServerRestartStartup>();
    }

    private static IEnumerable<CliHostOption> HostOptions(CliServerOptions options) =>
    [
        new("cliRoutes", "CLI routes", "The CLI's routes and sockets are served", options.CliRoutes),
        new("enableShell", "Shell", "Interactive shell sessions are accepted", options.EnableShell),
        new("shellAuth", "Shell access", "How shell sessions are authorized",
            !options.EnableShell ? "none"
            : options.Shell.AllowAnonymous ? "anonymous"
            : options.Shell.Authorize != null ? "callback"
            : !string.IsNullOrEmpty(options.Shell.Token) ? "token"
            : options.RequireAuthentication ? "users" : "locked"),
        new("maxTableRows", "Table rows", "Rows a table returns without paging (0: no limit)", options.MaxTableRows),
        new("protected", "Protected", "An auth provider protects every route", options.RequireAuthentication),
        new("externalAuthentication", "External sign-in", "The host application signs users in", options.ExternalAuthentication),
        new("mcp", "MCP", "The MCP endpoint is served", options.Mcp is { Enabled: true }),
    ];
}

/// <summary>Config contexts of the callers of the server.</summary>
public static class CliConfigContexts
{
    /// <summary>
    /// The context of a request: the caller's principal when signed in (on a protected server, or the admin dashboard's
    /// sign-in), else <see cref="Anonymous"/>.
    /// </summary>
    public static CliConfigContext Of(HttpContext? httpContext, CliServerOptions options, string via)
    {
        var principal = httpContext?.GetCliPrincipal();
        if (principal == null) return Anonymous(options, via);
        return new CliConfigContext(new CliConfigCaller(principal.UserId, principal.UserName, principal.Permissions),
            principal.Can, via);
    }

    /// <summary>
    /// A caller who is not signed in: nothing on a protected server; on a server without authentication every permission
    /// but <c>config:write</c> (unless <see cref="CliServerOptions.AllowAnonymousAdmin"/>).
    /// </summary>
    public static CliConfigContext Anonymous(CliServerOptions options, string via) =>
        new(null, permission => !options.RequireAuthentication && CliCommandPermissions.AnonymousCan(options, permission), via);
}

/// <summary>Creates the restart service when the server starts (its <c>startedAt</c>).</summary>
internal sealed class CliServerRestartStartup(CliServerRestart restart) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = restart.StartedAt;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Loads and applies the stored settings when the server starts.</summary>
internal sealed class CliConfigStartup(CliConfigRegistry registry, ILogger<CliConfigStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await registry.ReadyAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading the stored config settings failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
