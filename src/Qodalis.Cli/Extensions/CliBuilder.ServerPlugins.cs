using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Plugins;

namespace Qodalis.Cli.Extensions;

public partial class CliBuilder
{
    private CliServerPlugins? _serverPlugins;

    /// <summary>Options of the plugins installed at runtime from the marketplace (<see cref="ConfigureServerPlugins"/>).</summary>
    public CliServerPluginsOptions ServerPluginsOptions { get; } = new();

    /// <summary>
    /// The plugins installed at runtime (<c>&lt;data dir&gt;/server-plugins</c>), created with
    /// <see cref="ServerPluginsOptions"/> when first used: loaded by <c>AddCli</c> after the host's own registrations.
    /// </summary>
    public CliServerPlugins ServerPlugins => _serverPlugins ??= new CliServerPlugins(ServerPluginsOptions);

    /// <summary>
    /// Where the plugins installed from the marketplace are kept (<c>DataDir</c>, default <c>qcli-data</c> in the working
    /// directory; <c>Dir</c>, default <c>&lt;data dir&gt;/server-plugins</c>) and whether they are loaded (<c>Enabled</c>).
    /// </summary>
    /// <example>
    /// <code>
    /// cli.ConfigureServerPlugins(o =&gt; o.DataDir = Path.Combine(builder.Environment.ContentRootPath, "qcli-data"));
    /// </code>
    /// </example>
    public CliBuilder ConfigureServerPlugins(Action<CliServerPluginsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (_serverPlugins != null) throw new InvalidOperationException("the server plugins are configured before they are used");
        configure(ServerPluginsOptions);
        return this;
    }

    /// <summary>Lists a plugin among the installed ones (once per id): a plugin installed at runtime, described by its record.</summary>
    public CliBuilder AddInstalledPlugin(CliInstalledPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var existing = _installedPlugins.FindIndex(p => string.Equals(p.Id, plugin.Id, StringComparison.OrdinalIgnoreCase));
        if (existing < 0) _installedPlugins.Add(plugin);
        return this;
    }

    internal sealed record BuilderSnapshot(int Modules, int InstalledPlugins, int DashboardExtensions, int AssemblyParts,
        IFileStorageProvider? FileStorage, bool DataExplorer);

    /// <summary>What a plugin's registration may add, to take it back when it throws.</summary>
    internal BuilderSnapshot Snapshot() => new(_modules.Count, _installedPlugins.Count, _dashboardExtensions.Count,
        AdditionalAssemblyParts.Count, FileStorageProvider, HasDataExplorer);

    internal void Restore(BuilderSnapshot snapshot)
    {
        if (_modules.Count > snapshot.Modules) _modules.RemoveRange(snapshot.Modules, _modules.Count - snapshot.Modules);
        if (_installedPlugins.Count > snapshot.InstalledPlugins)
            _installedPlugins.RemoveRange(snapshot.InstalledPlugins, _installedPlugins.Count - snapshot.InstalledPlugins);
        if (_dashboardExtensions.Count > snapshot.DashboardExtensions)
            _dashboardExtensions.RemoveRange(snapshot.DashboardExtensions, _dashboardExtensions.Count - snapshot.DashboardExtensions);
        if (AdditionalAssemblyParts.Count > snapshot.AssemblyParts)
            AdditionalAssemblyParts.RemoveRange(snapshot.AssemblyParts, AdditionalAssemblyParts.Count - snapshot.AssemblyParts);
        FileStorageProvider = snapshot.FileStorage;
        HasDataExplorer = snapshot.DataExplorer;
    }
}
