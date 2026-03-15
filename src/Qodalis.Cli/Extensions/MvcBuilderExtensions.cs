using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Controllers;
using Qodalis.Cli.Jobs;
using Qodalis.Cli.Logging;
using Qodalis.Cli.Plugin.FileSystem;
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
        builder.Services.AddSingleton<CliLogSocketManager>();
        builder.Services.AddSingleton<ILoggerProvider, WebSocketLoggerProvider>();
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

        // Job storage provider
        if (cliBuilder.JobStorageProvider != null)
            builder.Services.AddSingleton<ICliJobStorageProvider>(cliBuilder.JobStorageProvider);
        else if (cliBuilder.JobStorageProviderType == null)
            builder.Services.AddSingleton<ICliJobStorageProvider>(new InMemoryJobStorageProvider());

        // Job scheduler
        builder.Services.AddSingleton<CliJobScheduler>(sp =>
        {
            var storage = sp.GetRequiredService<ICliJobStorageProvider>();
            var eventSocket = sp.GetRequiredService<CliEventSocketManager>();
            var logger = sp.GetRequiredService<ILogger<CliJobScheduler>>();
            var scheduler = new CliJobScheduler(storage, eventSocket, logger);

            foreach (var (job, jobType, options) in cliBuilder.JobRegistrations)
            {
                ICliJob resolvedJob;
                if (job != null)
                {
                    resolvedJob = job;
                }
                else
                {
                    resolvedJob = (ICliJob)sp.GetRequiredService(jobType!);
                }
                scheduler.Register(resolvedJob, options);
            }

            return scheduler;
        });

        builder.Services.AddHostedService(sp => sp.GetRequiredService<CliJobScheduler>());

        return builder;
    }
}
