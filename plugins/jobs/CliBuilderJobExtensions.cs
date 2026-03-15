using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Jobs;

public static class CliBuilderJobExtensions
{
    public static CliBuilder AddJob<T>(this CliBuilder builder, Action<CliJobOptions>? configure = null) where T : class, ICliJob
    {
        EnsureJobInfrastructure(builder);
        builder.Services.AddSingleton<T>();
        var options = new CliJobOptions();
        options.Name ??= typeof(T).Name;
        configure?.Invoke(options);
        GetRegistrations(builder).Add((null, typeof(T), options));
        return builder;
    }

    public static CliBuilder AddJob(this CliBuilder builder, ICliJob job, Action<CliJobOptions>? configure = null)
    {
        EnsureJobInfrastructure(builder);
        var options = new CliJobOptions();
        options.Name ??= job.GetType().Name;
        configure?.Invoke(options);
        GetRegistrations(builder).Add((job, null, options));
        return builder;
    }

    public static CliBuilder SetJobStorageProvider(this CliBuilder builder, ICliJobStorageProvider provider)
    {
        EnsureJobInfrastructure(builder);
        // Remove any previously registered storage provider
        RemoveService(builder.Services, typeof(ICliJobStorageProvider));
        builder.Services.AddSingleton<ICliJobStorageProvider>(provider);
        return builder;
    }

    public static CliBuilder SetJobStorageProvider<T>(this CliBuilder builder) where T : class, ICliJobStorageProvider
    {
        EnsureJobInfrastructure(builder);
        // Remove any previously registered storage provider
        RemoveService(builder.Services, typeof(ICliJobStorageProvider));
        builder.Services.AddSingleton<ICliJobStorageProvider, T>();
        return builder;
    }

    private static void EnsureJobInfrastructure(CliBuilder builder)
    {
        // Check if already initialized by looking for the state marker
        var descriptor = builder.Services.FirstOrDefault(d =>
            d.ServiceType == typeof(JobsPluginState));
        if (descriptor != null) return;

        var state = new JobsPluginState();
        builder.Services.AddSingleton(state);

        // Register the jobs plugin controller assembly
        builder.AddApplicationPart(typeof(CliJobsController).Assembly);

        // Default in-memory storage (can be overridden by SetJobStorageProvider)
        builder.Services.AddSingleton<ICliJobStorageProvider>(new InMemoryJobStorageProvider());

        // Register the scheduler factory — captures the registrations list by reference
        // so all AddJob calls that happen after this will still be included
        var registrations = state.Registrations;
        builder.Services.AddSingleton<CliJobScheduler>(sp =>
        {
            var storage = sp.GetRequiredService<ICliJobStorageProvider>();
            var eventSocket = sp.GetRequiredService<CliEventSocketManager>();
            var logger = sp.GetRequiredService<ILogger<CliJobScheduler>>();
            var scheduler = new CliJobScheduler(storage, eventSocket, logger);

            foreach (var (job, jobType, options) in registrations)
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

        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CliJobScheduler>());
    }

    private static List<(ICliJob? Job, Type? JobType, CliJobOptions Options)> GetRegistrations(CliBuilder builder)
    {
        var descriptor = builder.Services.FirstOrDefault(d =>
            d.ServiceType == typeof(JobsPluginState));
        var state = (JobsPluginState)descriptor!.ImplementationInstance!;
        return state.Registrations;
    }

    private static void RemoveService(IServiceCollection services, Type serviceType)
    {
        var descriptors = services.Where(d => d.ServiceType == serviceType).ToList();
        foreach (var d in descriptors)
            services.Remove(d);
    }
}

internal class JobsPluginState
{
    public List<(ICliJob? Job, Type? JobType, CliJobOptions Options)> Registrations { get; } = [];
}
