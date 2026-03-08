using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Controllers;
using Qodalis.Cli.FileSystem;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Extensions;

public static class MvcBuilderExtensions
{
    public static IMvcBuilder AddCli(this IMvcBuilder builder, Action<CliBuilder>? configure = null)
    {
        builder.PartManager.ApplicationParts
            .Add(new AssemblyPart(typeof(CliController).Assembly));

        var cliBuilder = new CliBuilder(builder.Services);
        configure?.Invoke(cliBuilder);

        builder.Services.AddSingleton<ICliCommandRegistry>(sp =>
        {
            var registry = new CliCommandRegistry();
            var processors = sp.GetServices<ICliCommandProcessor>();
            foreach (var processor in processors)
            {
                registry.Register(processor);
            }
            return registry;
        });

        builder.Services.AddSingleton<ICliCommandExecutorService, CliCommandExecutorService>();
        builder.Services.AddSingleton<CliEventSocketManager>();
        builder.Services.AddSingleton<ShellSessionManager>();

        // Register IFileStorageProvider as singleton
        if (cliBuilder.FileStorageProvider != null)
        {
            builder.Services.AddSingleton<IFileStorageProvider>(cliBuilder.FileStorageProvider);
        }
        else
        {
            builder.Services.AddSingleton<IFileStorageProvider>(new InMemoryFileStorageProvider());
        }

        return builder;
    }
}
