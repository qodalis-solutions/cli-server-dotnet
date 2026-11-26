using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Config;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>
/// Loads the installed marketplace plugins when the server starts and registers the <c>dashboard-plugins</c> config
/// provider (their settings; its store: the host's store for <c>dashboard-plugins</c>, else the default store), unless
/// the marketplace is off or the host registered a provider with that id.
/// </summary>
internal sealed class DashboardMarketplaceStartup(
    DashboardMarketplace marketplace,
    CliConfigRegistry? registry,
    ServerPluginMarketplace? serverMarketplace = null,
    ILogger? logger = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        marketplace.Load();
        if (marketplace.Enabled && registry != null && !registry.Has(marketplace.Config.Id)) registry.Add(marketplace.Config);
        WarnIfUnwritable();
        return Task.CompletedTask;
    }

    /// <summary>One WARNING when installing would fail: a plugins folder that cannot be created or written.</summary>
    private void WarnIfUnwritable()
    {
        var folders = new List<(string Dir, string Hint)>();
        if (marketplace.Enabled && marketplace.Install != "off") folders.Add((marketplace.Dir, marketplace.FolderHint));
        if (serverMarketplace is { PluginsEnabled: true } server && server.Install != "off") folders.Add((server.Dir, server.FolderHint));
        if (PluginsFolder.UnwritableWarning(folders) is { } warning)
            (logger ?? NullLogger.Instance).LogWarning("[qcli-admin] WARNING: {Warning}", warning);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Serves the marketplace plugins' files at <c>/qcli/modules/&lt;id&gt;/</c> before the rest of the pipeline, to anyone
/// (the dashboard's <c>import()</c> sends no token), like the built-in dashboard modules.
/// </summary>
internal sealed class DashboardMarketplaceAssets : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var cliRoutes = app.ApplicationServices.GetService<Qodalis.Cli.Security.CliServerOptions>()?.CliRoutes != false;
        var marketplace = app.ApplicationServices.GetService<DashboardMarketplace>();
        if (cliRoutes && marketplace is { Enabled: true })
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (!await marketplace.TryServeAsync(context)) await nextMiddleware();
            });
        }
        next(app);
    };
}
