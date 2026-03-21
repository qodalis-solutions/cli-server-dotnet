using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.FileSystem;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Extensions;

public class CliBuilder
{
    private readonly List<ICliModule> _modules = [];

    internal IFileStorageProvider? FileStorageProvider { get; private set; }
    internal bool HasDataExplorer { get; private set; }
    internal List<Assembly> AdditionalAssemblyParts { get; } = [];

    public IServiceCollection Services { get; }

    /// <summary>
    /// Returns all registered modules.
    /// </summary>
    public IReadOnlyList<ICliModule> Modules => _modules;

    internal CliBuilder(IServiceCollection services)
    {
        Services = services;
    }

    public CliBuilder AddProcessor<T>() where T : class, ICliCommandProcessor
    {
        Services.AddSingleton<ICliCommandProcessor, T>();
        return this;
    }

    public CliBuilder AddProcessor(ICliCommandProcessor processor)
    {
        Services.AddSingleton<ICliCommandProcessor>(processor);
        return this;
    }

    public CliBuilder AddProcessorsFromAssembly(Assembly assembly)
    {
        var processorTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(ICliCommandProcessor).IsAssignableFrom(t));

        foreach (var type in processorTypes)
        {
            Services.AddSingleton(typeof(ICliCommandProcessor), type);
        }

        return this;
    }

    public CliBuilder AddModule(ICliModule module)
    {
        _modules.Add(module);

        foreach (var processor in module.Processors)
        {
            Services.AddSingleton<ICliCommandProcessor>(processor);
        }

        return this;
    }

    /// <summary>
    /// Registers an additional assembly to be included as an MVC application part,
    /// enabling controller discovery from plugins.
    /// </summary>
    public CliBuilder AddApplicationPart(Assembly assembly)
    {
        AdditionalAssemblyParts.Add(assembly);
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

        Services.AddSingleton(options);
        Services.AddSingleton<FileSystemPathValidator>();
        return this;
    }

    public CliBuilder AddDataExplorerProvider<T>(Action<DataExplorerProviderOptions> configure)
        where T : class, IDataExplorerProvider
    {
        HasDataExplorer = true;

        var options = new DataExplorerProviderOptions
        {
            Name = typeof(T).Name,
            Description = typeof(T).Name
        };
        configure(options);

        Services.AddSingleton(new DataExplorerProviderRegistration
        {
            ProviderType = typeof(T),
            Options = options
        });

        Services.AddSingleton<T>();

        return this;
    }

    public CliBuilder AddDataExplorerProvider(IDataExplorerProvider provider, Action<DataExplorerProviderOptions> configure)
    {
        HasDataExplorer = true;

        var options = new DataExplorerProviderOptions
        {
            Name = provider.GetType().Name,
            Description = provider.GetType().Name
        };
        configure(options);

        Services.AddSingleton(new DataExplorerProviderRegistration
        {
            ProviderInstance = provider,
            Options = options
        });

        return this;
    }
}
