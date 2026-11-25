using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>Loads the installed marketplace plugins when the server starts.</summary>
internal sealed class DashboardMarketplaceStartup(DashboardMarketplace marketplace) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        marketplace.Load();
        return Task.CompletedTask;
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
