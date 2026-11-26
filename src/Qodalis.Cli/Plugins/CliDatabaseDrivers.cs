using System.Reflection;
using Qodalis.Cli.Abstractions.Config;

namespace Qodalis.Cli.Plugins;

/// <summary>A database driver plugin registered on the server (<see cref="CliDatabaseDrivers"/>).</summary>
public sealed class CliDatabaseDriver
{
    /// <summary>The dialect it serves (<c>postgres</c>, <c>mssql</c>), as the databases plugin and the EF Core stores name it.</summary>
    public required string Dialect { get; init; }

    /// <summary>The plugin that registered it (<c>database-postgres</c>).</summary>
    public required string Plugin { get; init; }

    /// <summary>
    /// Its connection string, used by the databases plugin and the EF Core stores of this dialect that set none of their
    /// own; null while it has none.
    /// </summary>
    public string? ConnectionString { get; init; }
}

/// <summary>
/// The database drivers installed on the server (<c>CliBuilder.DatabaseDrivers</c>): a driver plugin
/// (<see cref="CliDatabaseDriverPluginEntry"/>) registers its dialect and connection here at
/// <see cref="CliPluginOrder.Driver"/>, then the databases plugin and the EF Core stores read it while they register.
/// </summary>
public sealed class CliDatabaseDrivers
{
    private readonly Dictionary<string, CliDatabaseDriver> _drivers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The plugin that brings each dialect's driver from the marketplace.</summary>
    public static readonly IReadOnlyDictionary<string, string> Plugins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["postgres"] = "database-postgres",
        ["mssql"] = "database-mssql",
    };

    /// <summary>The drivers registered so far.</summary>
    public IReadOnlyList<CliDatabaseDriver> All
    {
        get
        {
            lock (_drivers) return [.. _drivers.Values];
        }
    }

    /// <summary>Registers (or replaces) the driver of its dialect.</summary>
    public void Add(CliDatabaseDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentException.ThrowIfNullOrWhiteSpace(driver.Dialect);
        lock (_drivers) _drivers[driver.Dialect] = driver;
    }

    /// <summary>The driver of <paramref name="dialect"/>; null when none is registered.</summary>
    public CliDatabaseDriver? Find(string dialect)
    {
        lock (_drivers) return _drivers.GetValueOrDefault(dialect);
    }

    /// <summary>The connection string of <paramref name="dialect"/>'s driver; null when there is no driver or it has none.</summary>
    public string? ConnectionString(string dialect) => Find(dialect)?.ConnectionString is { Length: > 0 } c ? c : null;

    /// <summary>
    /// What a plugin of <paramref name="dialect"/> without a connection of its own needs: its <c>settings.connectionString</c>,
    /// or for a dialect that has a driver plugin, that plugin's connection.
    /// </summary>
    public static string MissingConnection(string dialect) =>
        Plugins.TryGetValue(dialect, out var plugin) ? $"settings.connectionString or the {plugin} plugin's connection" : "settings.connectionString";

    /// <summary>How to get <paramref name="dialect"/>'s driver: its plugin from the marketplace, or <paramref name="package"/> in the host.</summary>
    public static string InstallHint(string dialect, string package) =>
        Plugins.TryGetValue(dialect, out var plugin)
            ? $"{package}: install the {plugin} plugin, or add the package to the host"
            : $"{package}: add it to the host, or install it with the plugin";
}

/// <summary>
/// The entry of a database driver plugin (<c>Qodalis.Cli.Plugin.Database.Postgres</c>, <c>.SqlServer</c>): a package that
/// brings a dialect's ADO.NET driver and EF Core provider, so the databases plugin and the EF Core stores installed from
/// the marketplace can use that database. It shares its folder's assemblies with the other plugins
/// (<see cref="CliPluginContext.ShareAssemblies"/>), registers the dialect on <c>CliBuilder.DatabaseDrivers</c> and
/// keeps one setting, the connection the databases plugin and the stores of that dialect use unless they set their
/// own (a required secret: without it the plugin is <c>needs-configuration</c>, its driver still usable with the stores'
/// own connections). Declare it with <c>Order = CliPluginOrder.Driver</c>.
/// </summary>
public abstract class CliDatabaseDriverPluginEntry : ICliPluginEntry
{
    /// <param name="dialect">The dialect (<c>postgres</c>, <c>mssql</c>).</param>
    /// <param name="title">The database's name (<c>PostgreSQL</c>).</param>
    /// <param name="assemblies">The driver's assemblies (<c>Npgsql</c>, <c>Npgsql.EntityFrameworkCore.PostgreSQL</c>), checked while it registers.</param>
    protected CliDatabaseDriverPluginEntry(string dialect, string title, params string[] assemblies)
    {
        Dialect = dialect;
        Title = title;
        Assemblies = assemblies;
    }

    /// <summary>The dialect it serves.</summary>
    public string Dialect { get; }

    /// <summary>The database's name.</summary>
    public string Title { get; }

    /// <summary>The driver's assemblies.</summary>
    public IReadOnlyList<string> Assemblies { get; }

    /// <summary>Its settings: the shared connection.</summary>
    public IReadOnlyList<CliConfigSettingDefinition> Settings =>
    [
        new()
        {
            Key = "connectionString", Label = "Connection",
            Description = $"The {Title} connection string of the databases plugin and the EF Core stores that use the {Dialect} dialect without a connection of their own; kept as a secret",
            Type = CliConfigEntryTypes.Secret, Required = true, RequiresRestart = true,
        },
    ];

    public void Register(CliPluginContext context)
    {
        context.ShareAssemblies();
        // A broken install fails the plugin now, with the assembly it lacks, not the stores later
        var own = GetType().Assembly;
        foreach (var name in Assemblies)
        {
            if (!CliPluginLoadContext.HostHas(name) && !InFolder(own, name))
                throw new InvalidOperationException($"the {Dialect} driver is incomplete: {name} is missing (reinstall the {context.PluginId} plugin)");
        }
        var settings = context.BootSettings(new CliPluginSettingsOptions
        {
            Title = $"{Title} driver", Description = $"The {Title} driver of the databases plugin and the EF Core stores", Settings = [.. Settings],
        });
        var connection = settings.Text("connectionString");
        context.Builder.DatabaseDrivers.Add(new CliDatabaseDriver
        {
            Dialect = Dialect, Plugin = context.PluginId, ConnectionString = connection.Length > 0 ? connection : null,
        });
    }

    private static bool InFolder(Assembly own, string name) =>
        System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(own) is CliPluginLoadContext context
        && context.ResolveManaged(new AssemblyName(name)) != null;
}
