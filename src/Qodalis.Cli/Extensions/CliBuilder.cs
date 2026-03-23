using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.FileSystem;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Extensions;

/// <summary>
/// Fluent builder for configuring CLI services, including command processors, modules, filesystem, and data explorer providers.
/// </summary>
public class CliBuilder
{
    private readonly List<ICliModule> _modules = [];

    internal IFileStorageProvider? FileStorageProvider { get; private set; }
    internal bool HasDataExplorer { get; private set; }
    internal List<Assembly> AdditionalAssemblyParts { get; } = [];

    /// <summary>
    /// Gets the service collection for registering additional services.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Returns all registered modules.
    /// </summary>
    public IReadOnlyList<ICliModule> Modules => _modules;

    internal CliBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>
    /// Registers a command processor by type.
    /// </summary>
    /// <typeparam name="T">The command processor type.</typeparam>
    /// <returns>This builder instance for chaining.</returns>
    public CliBuilder AddProcessor<T>() where T : class, ICliCommandProcessor
    {
        Services.AddSingleton<ICliCommandProcessor, T>();
        return this;
    }

    /// <summary>
    /// Registers a command processor instance.
    /// </summary>
    /// <param name="processor">The processor instance to register.</param>
    /// <returns>This builder instance for chaining.</returns>
    public CliBuilder AddProcessor(ICliCommandProcessor processor)
    {
        Services.AddSingleton<ICliCommandProcessor>(processor);
        return this;
    }

    /// <summary>
    /// Discovers and registers all <see cref="ICliCommandProcessor"/> implementations from the specified assembly.
    /// </summary>
    /// <param name="assembly">The assembly to scan for command processors.</param>
    /// <returns>This builder instance for chaining.</returns>
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

    /// <summary>
    /// Registers a CLI module and all its command processors.
    /// </summary>
    /// <param name="module">The module to register.</param>
    /// <returns>This builder instance for chaining.</returns>
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

    /// <summary>
    /// Enables the filesystem API with optional configuration for allowed paths and custom providers.
    /// </summary>
    /// <param name="configure">Optional action to configure filesystem options.</param>
    /// <returns>This builder instance for chaining.</returns>
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

    /// <summary>
    /// Registers a data explorer provider by type with the specified options.
    /// </summary>
    /// <typeparam name="T">The data explorer provider type.</typeparam>
    /// <param name="configure">Action to configure the provider options (name, description, language, etc.).</param>
    /// <returns>This builder instance for chaining.</returns>
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

    /// <summary>
    /// Registers a data explorer provider instance with the specified options.
    /// </summary>
    /// <param name="provider">The provider instance.</param>
    /// <param name="configure">Action to configure the provider options.</param>
    /// <returns>This builder instance for chaining.</returns>
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
