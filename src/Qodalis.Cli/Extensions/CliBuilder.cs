using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.FileSystem;
using Qodalis.Cli.Plugin.FileSystem;

namespace Qodalis.Cli.Extensions;

public class CliBuilder
{
    private readonly IServiceCollection _services;

    internal IFileStorageProvider? FileStorageProvider { get; private set; }
    internal List<(ICliJob? Job, Type? JobType, CliJobOptions Options)> JobRegistrations { get; } = [];
    internal ICliJobStorageProvider? JobStorageProvider { get; private set; }
    internal Type? JobStorageProviderType { get; private set; }

    internal CliBuilder(IServiceCollection services)
    {
        _services = services;
    }

    public CliBuilder AddProcessor<T>() where T : class, ICliCommandProcessor
    {
        _services.AddSingleton<ICliCommandProcessor, T>();
        return this;
    }

    public CliBuilder AddProcessor(ICliCommandProcessor processor)
    {
        _services.AddSingleton<ICliCommandProcessor>(processor);
        return this;
    }

    public CliBuilder AddProcessorsFromAssembly(Assembly assembly)
    {
        var processorTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(ICliCommandProcessor).IsAssignableFrom(t));

        foreach (var type in processorTypes)
        {
            _services.AddSingleton(typeof(ICliCommandProcessor), type);
        }

        return this;
    }

    public CliBuilder AddModule(ICliModule module)
    {
        foreach (var processor in module.Processors)
        {
            _services.AddSingleton<ICliCommandProcessor>(processor);
        }

        return this;
    }

    public CliBuilder AddJob<T>(Action<CliJobOptions>? configure = null) where T : class, ICliJob
    {
        _services.AddSingleton<T>();
        var options = new CliJobOptions();
        options.Name ??= typeof(T).Name;
        configure?.Invoke(options);
        JobRegistrations.Add((null, typeof(T), options));
        return this;
    }

    public CliBuilder AddJob(ICliJob job, Action<CliJobOptions>? configure = null)
    {
        var options = new CliJobOptions();
        options.Name ??= job.GetType().Name;
        configure?.Invoke(options);
        JobRegistrations.Add((job, null, options));
        return this;
    }

    public CliBuilder SetJobStorageProvider(ICliJobStorageProvider provider)
    {
        JobStorageProvider = provider;
        return this;
    }

    public CliBuilder SetJobStorageProvider<T>() where T : class, ICliJobStorageProvider
    {
        JobStorageProviderType = typeof(T);
        _services.AddSingleton<ICliJobStorageProvider, T>();
        return this;
    }

    public CliBuilder AddFileSystem(Action<FileSystemOptions>? configure = null)
    {
        var options = new FileSystemOptions();
        configure?.Invoke(options);

        if (options.Provider != null)
        {
            FileStorageProvider = options.Provider;
        }
        else if (options.AllowedPaths.Count > 0)
        {
            FileStorageProvider = new OsFileStorageProvider(new OsProviderOptions
            {
                AllowedPaths = options.AllowedPaths
            });
        }
        else
        {
            FileStorageProvider = new InMemoryFileStorageProvider();
        }

        _services.AddSingleton(options);
        _services.AddSingleton<FileSystemPathValidator>();
        return this;
    }
}
