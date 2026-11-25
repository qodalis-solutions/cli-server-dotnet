using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.FileSystem;
using Qodalis.Cli.Mcp;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Extensions;

/// <summary>
/// Fluent builder for configuring CLI services, including command processors, modules, filesystem, and data explorer providers.
/// </summary>
public partial class CliBuilder
{
    private readonly List<ICliModule> _modules = [];
    private readonly List<CliInstalledPlugin> _installedPlugins = [];
    private readonly List<CliDashboardExtension> _dashboardExtensions = [];
    private readonly CliPermissionCatalog _permissions = new();
    private readonly HashSet<string> _permissionSources = new(StringComparer.Ordinal);

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

    /// <summary>
    /// The server plugin packages installed so far, shown on the admin dashboard's plugins page.
    /// </summary>
    public IReadOnlyList<CliInstalledPlugin> InstalledPlugins => _installedPlugins;

    /// <summary>The dashboard extensions registered so far, in registration order.</summary>
    public IReadOnlyList<CliDashboardExtension> DashboardExtensions => _dashboardExtensions;

    /// <summary>
    /// Adds a page to the admin dashboard: the dashboard lists it in its navigation and loads
    /// <see cref="CliDashboardExtension.Entry"/> (an ES module the plugin serves) when it is opened. Served by the
    /// admin plugin at <c>GET /api/v1/qcli/dashboard/extensions</c>, in registration order. Throws
    /// <see cref="ArgumentException"/> <c>invalid dashboard extension: &lt;reason&gt;</c> or
    /// <see cref="InvalidOperationException"/> <c>dashboard extension already registered: &lt;id&gt;</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// cli.AddDashboardExtension(new CliDashboardExtension
    /// {
    ///     Id = "billing", Title = "Billing", Icon = "💳", Group = "data", Permission = "billing:read",
    ///     Entry = "/billing/admin/extension.js", Styles = ["/billing/admin/extension.css"],
    /// });
    /// </code>
    /// </example>
    public CliBuilder AddDashboardExtension(CliDashboardExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        var valid = extension.Validate();
        if (_dashboardExtensions.Any(e => e.Id == valid.Id))
            throw new InvalidOperationException($"dashboard extension already registered: {valid.Id}");
        _dashboardExtensions.Add(valid);
        return this;
    }

    /// <summary>
    /// Lists the package <paramref name="assembly"/> belongs to among the installed plugins (once). The built-in
    /// plugins (users, admin, jobs, storage providers, ...) call it themselves; call it from a plugin of your own.
    /// The host application's own assembly is never listed. The package's icon is read from its
    /// <c>QodalisCliIcon</c> assembly metadata (see <see cref="CliInstalledPlugin.IconMetadataKey"/>).
    /// </summary>
    public CliBuilder AddInstalledPlugin(Assembly assembly)
    {
        if (assembly == Assembly.GetEntryAssembly() || assembly.IsDynamic)
            return this;

        var plugin = CliInstalledPlugin.FromAssembly(assembly);
        if (_installedPlugins.All(p => p.Id != plugin.Id))
            _installedPlugins.Add(plugin);
        return this;
    }

    /// <summary>
    /// Server-wide security options (shell, allowed origins, authentication requirement).
    /// </summary>
    public CliServerOptions ServerOptions { get; } = new();

    /// <summary>
    /// Leaves the CLI's HTTP routes and sockets out (<see cref="CliServerOptions.CliRoutes"/>): for a second service that
    /// only validates tokens and serves the host's own routes (with the users plugin in guard-only mode).
    /// </summary>
    public CliBuilder WithoutCliRoutes()
    {
        ServerOptions.CliRoutes = false;
        return this;
    }

    /// <summary>
    /// Enables (or disables) the interactive shell WebSockets. Disabled by default.
    /// Enabling alone does not grant access: with the users plugin the session needs
    /// <c>shell:execute</c>; without it configure a token, an authorize callback or
    /// <c>AllowAnonymous</c> via <see cref="EnableShell(Action{CliShellOptions})"/>,
    /// otherwise the shell stays locked (every connection is closed with 4401).
    /// </summary>
    public CliBuilder EnableShell(bool enabled = true)
    {
        ServerOptions.EnableShell = enabled;
        return this;
    }

    /// <summary>
    /// Enables the interactive shell and configures its access mode, limits and environment.
    /// </summary>
    /// <example>
    /// <code>
    /// cli.EnableShell(o =&gt;
    /// {
    ///     o.Token = builder.Configuration["Shell:Token"];
    ///     o.IdleTimeout = TimeSpan.FromMinutes(10);
    /// });
    /// </code>
    /// </example>
    public CliBuilder EnableShell(Action<CliShellOptions> configure)
    {
        ServerOptions.EnableShell = true;
        configure(ServerOptions.Shell);
        return this;
    }

    /// <summary>
    /// Restricts CORS and WebSocket upgrades to the given origins (e.g. <c>http://localhost:4300</c>,
    /// <c>http://127.0.0.1:*</c>). Registers the default CORS policy for <c>app.UseCors()</c>.
    /// Without it the library configures no CORS.
    /// </summary>
    public CliBuilder SetAllowedOrigins(params string[] origins)
    {
        ServerOptions.AllowedOrigins.Clear();
        ServerOptions.AllowedOrigins.AddRange(origins.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()));
        return this;
    }

    /// <summary>
    /// Enables the MCP endpoint (<c>POST /api/qcli/mcp</c>, MCP Streamable HTTP, stateless, JSON responses):
    /// AI clients call the registered commands as tools with the caller's permissions. Off by default.
    /// With the users plugin the endpoint takes session or API tokens (<c>commands:execute</c>); without an
    /// authentication provider set <see cref="McpOptions.Token"/> to require a static bearer token.
    /// </summary>
    /// <example>
    /// <code>
    /// cli.AddMcp(o =&gt;
    /// {
    ///     o.Exclude = ["aws_*"];
    ///     o.Instructions = "Tools run commands on the Qodalis CLI server.";
    /// });
    /// </code>
    /// </example>
    public CliBuilder AddMcp(Action<McpOptions>? configure = null)
    {
        var options = new McpOptions();
        configure?.Invoke(options);
        ServerOptions.Mcp = options;
        return this;
    }

    internal CliBuilder(IServiceCollection services)
    {
        Services = services;
        AddInstalledPlugin(typeof(CliBuilder).Assembly);
        AddPermissions(CliPermissionCatalog.AddCoreEntries, CliPermissionSurface.Cli);
        Services.AddSingleton<ICliPermissionCatalog>(_ => _permissions.Freeze());
    }

    /// <summary>
    /// Adds permissions to the catalog users choose from when they grant permissions (the dashboard's and the
    /// console's pickers, <c>GET /api/v1/qcli/permissions</c>). Entries default to the <c>host</c> surface: the
    /// permissions the host application's own routes check, the only ones a <c>host</c> API token may carry.
    /// Plugins pass <see cref="CliPermissionSurface.Cli"/>. Startup only; an invalid or duplicate entry throws.
    /// </summary>
    /// <example>
    /// <code>
    /// cli.AddPermissions(p =&gt; p.Group("Rates")
    ///     .Add("rates:read", "Official exchange rates")
    ///     .Add("banks:read", "Banks and their buy and sell rates"));
    /// </code>
    /// </example>
    public CliBuilder AddPermissions(Action<CliPermissionCatalogBuilder> configure, string surface = CliPermissionSurface.Host)
    {
        var builder = new CliPermissionCatalogBuilder(surface);
        configure(builder);
        _permissions.AddRange(builder.Entries);
        return this;
    }

    /// <summary>
    /// <see cref="AddPermissions(Action{CliPermissionCatalogBuilder}, string)"/> once per <paramref name="source"/> on this
    /// builder: for plugins whose <c>Add*</c> method may run twice. Entries that collide with another source still throw.
    /// </summary>
    public CliBuilder AddPermissionsOnce(string source, Action<CliPermissionCatalogBuilder> configure,
        string surface = CliPermissionSurface.Cli)
    {
        if (!_permissionSources.Add(source)) return this;
        return AddPermissions(configure, surface);
    }

    /// <summary>
    /// Declares the permissions a plugin's routes need on a protected server: <paramref name="read"/> for GET and
    /// HEAD under <paramref name="path"/>, <paramref name="write"/> (default <paramref name="read"/>) for the other
    /// methods. The longest declared path wins, and declared paths win over the core table.
    /// </summary>
    /// <param name="path">A path under a CLI prefix (<c>/api/v1/qcli/...</c>, <c>/api/qcli/...</c>).</param>
    /// <param name="read">Permission for GET and HEAD.</param>
    /// <param name="write">Permission for the other methods.</param>
    /// <exception cref="ArgumentException">The path is not under a CLI prefix.</exception>
    public CliBuilder AddRoutePermission(string path, string read, string? write = null)
    {
        var normalized = (path ?? string.Empty).TrimEnd('/').ToLowerInvariant();
        if (!normalized.StartsWith('/') || !CliRoutePolicy.IsCliPath(normalized) || normalized.StartsWith("/ws/", StringComparison.Ordinal))
            throw new ArgumentException($"invalid route permission: {path}", nameof(path));
        if (ServerOptions.RoutePermissions.All(r => r.Path != normalized))
            ServerOptions.RoutePermissions.Add(new CliRoutePermission(normalized, read, write));
        return this;
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
    /// Registers a CLI module, its command processors and its config providers (<see cref="ICliModule.ConfigProviders"/>).
    /// </summary>
    /// <param name="module">The module to register.</param>
    /// <returns>This builder instance for chaining.</returns>
    public CliBuilder AddModule(ICliModule module)
    {
        _modules.Add(module);
        AddInstalledPlugin(module.GetType().Assembly);

        foreach (var processor in module.Processors)
        {
            Services.AddSingleton<ICliCommandProcessor>(processor);
        }

        foreach (var provider in module.ConfigProviders ?? [])
        {
            AddConfigProvider(provider);
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
        AddInstalledPlugin(typeof(FileSystemOptions).Assembly);
        AddInstalledPlugin(FileStorageProvider.GetType().Assembly);
        // The dashboard's Filesystem page, when this package carries its build
        AddDashboardModule(CliDashboardModules.FileSystem, typeof(CliBuilder).Assembly);
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
        AddInstalledPlugin(typeof(T).Assembly);
        // The dashboard's Data explorer page (once), when this package carries its build
        AddDashboardModule(CliDashboardModules.DataExplorer, typeof(CliBuilder).Assembly);

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
        AddInstalledPlugin(provider.GetType().Assembly);
        AddDashboardModule(CliDashboardModules.DataExplorer, typeof(CliBuilder).Assembly);

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
