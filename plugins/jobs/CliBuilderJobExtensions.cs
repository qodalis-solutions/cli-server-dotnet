using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// Extension methods for registering jobs and job infrastructure on <see cref="CliBuilder"/>.
/// </summary>
public static class CliBuilderJobExtensions
{
    /// <summary>
    /// Registers a job of type <typeparamref name="T"/> with the scheduler.
    /// </summary>
    /// <typeparam name="T">The job implementation type.</typeparam>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="configure">Optional callback to configure job options.</param>
    /// <returns>The builder for chaining.</returns>
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

    /// <summary>
    /// Registers an existing job instance with the scheduler.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="job">The job instance to register.</param>
    /// <param name="configure">Optional callback to configure job options.</param>
    /// <returns>The builder for chaining.</returns>
    public static CliBuilder AddJob(this CliBuilder builder, ICliJob job, Action<CliJobOptions>? configure = null)
    {
        EnsureJobInfrastructure(builder);
        var options = new CliJobOptions();
        options.Name ??= job.GetType().Name;
        configure?.Invoke(options);
        GetRegistrations(builder).Add((job, null, options));
        return builder;
    }

    /// <summary>
    /// Replaces the job storage provider with the specified instance.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="provider">The storage provider instance to use.</param>
    /// <returns>The builder for chaining.</returns>
    public static CliBuilder SetJobStorageProvider(this CliBuilder builder, ICliJobStorageProvider provider)
    {
        EnsureJobInfrastructure(builder);
        // Remove any previously registered storage provider
        RemoveService(builder.Services, typeof(ICliJobStorageProvider));
        builder.Services.AddSingleton<ICliJobStorageProvider>(provider);
        return builder;
    }

    /// <summary>
    /// Replaces the job storage provider with a type registered via DI.
    /// </summary>
    /// <typeparam name="T">The storage provider implementation type.</typeparam>
    /// <param name="builder">The CLI builder instance.</param>
    /// <returns>The builder for chaining.</returns>
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
            var eventSocket = sp.GetRequiredService<ICliEventSocketManager>();
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

/// <summary>
/// Internal state holder for job registrations collected during service configuration.
/// </summary>
internal class JobsPluginState
{
    /// <summary>
    /// Gets the list of job registrations accumulated by <see cref="CliBuilderJobExtensions.AddJob{T}"/> calls.
    /// </summary>
    public List<(ICliJob? Job, Type? JobType, CliJobOptions Options)> Registrations { get; } = [];
}
