using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Controllers;
using Qodalis.Cli.Logging;
using Qodalis.Cli.Plugin.FileSystem;
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
        builder.PartManager.ApplicationParts
            .Add(new AssemblyPart(typeof(CliController).Assembly));

        var cliBuilder = new CliBuilder(builder.Services);
        configure?.Invoke(cliBuilder);

        // Add any additional assembly parts registered by plugins
        foreach (var assembly in cliBuilder.AdditionalAssemblyParts)
        {
            builder.PartManager.ApplicationParts
                .Add(new AssemblyPart(assembly));
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
            return registry;
        });

        builder.Services.AddSingleton<ICliCommandExecutorService, CliCommandExecutorService>();
        builder.Services.AddSingleton<ICliServerInfoService, CliServerInfoService>();
        builder.Services.AddSingleton<ICliEventSocketManager, CliEventSocketManager>();
        builder.Services.AddSingleton<ICliLogSocketManager, CliLogSocketManager>();
        builder.Services.AddSingleton<ILoggerProvider, WebSocketLoggerProvider>();
        builder.Services.AddSingleton<IShellSessionManager, ShellSessionManager>();

        // Register IFileStorageProvider as singleton
        if (cliBuilder.FileStorageProvider != null)
        {
            builder.Services.AddSingleton<IFileStorageProvider>(cliBuilder.FileStorageProvider);
        }
        else
        {
            builder.Services.AddSingleton<IFileStorageProvider>(new InMemoryFileStorageProvider());
        }

        // Register DataExplorer services (always register so the controller can resolve)
        builder.Services.AddSingleton<DataExplorerRegistry>(sp =>
        {
            var registry = new DataExplorerRegistry();
            var registrations = sp.GetServices<DataExplorerProviderRegistration>();

            foreach (var registration in registrations)
            {
                IDataExplorerProvider provider;
                if (registration.ProviderInstance != null)
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

                registry.Register(provider, registration.Options);
            }

            return registry;
        });
        builder.Services.AddSingleton<IDataExplorerRegistry>(sp => sp.GetRequiredService<DataExplorerRegistry>());

        builder.Services.AddSingleton<IDataExplorerExecutorService, DataExplorerExecutorService>();

        return builder;
    }
}
