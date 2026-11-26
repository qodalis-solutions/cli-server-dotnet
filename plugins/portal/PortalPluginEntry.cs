using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Plugins;
using Qodalis.Cli.Portal;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.Portal.PortalPluginEntry), Id = "portal", Order = 60)]

namespace Qodalis.Cli.Plugin.Portal;

/// <summary>
/// The portal from the dashboard (or <c>cli.AddPlugin&lt;PortalPluginEntry&gt;(new { path = "/docs" })</c>): the
/// prebuilt portal site showing the docs plugin's guides and the public-api plugin's reference. Loads at order 60, after
/// what it shows: without either it is <c>needs-configuration</c>. Its path, name and logo are settings read while the
/// server starts; the branding stays its <c>portal</c> config provider's. <c>siteDir</c> (code only) serves a site folder.
/// </summary>
public sealed class PortalPluginEntry : ICliPluginEntry
{
    /// <summary>Its settings (code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings =>
    [
        new()
        {
            Key = "path", Label = "Path", Description = "Where the portal is served (/ for the root)", Type = CliConfigEntryTypes.String, Default = "/qcli/portal",
            RequiresRestart = true, Validation = new CliConfigValidation { Required = true, Pattern = "^/([A-Za-z0-9._~-]+(/[A-Za-z0-9._~-]+)*)?$" },
        },
        new() { Key = "name", Label = "Name", Type = CliConfigEntryTypes.String, Default = "Documentation", RequiresRestart = true, Validation = new CliConfigValidation { Required = true, Max = 80 } },
        new()
        {
            Key = "logoUrl", Label = "Logo", Description = "An http(s) URL or a root-relative path; empty: none", Type = CliConfigEntryTypes.String, Default = "",
            RequiresRestart = true, Validation = new CliConfigValidation { Max = 500 },
        },
    ];

    public void Register(CliPluginContext context)
    {
        var settings = context.BootSettings(new CliPluginSettingsOptions
        {
            Id = "portal-plugin", Title = "Portal (setup)", Description = "Where the portal is served", Settings = [.. Settings],
        });
        if (!context.Services.Any(d => d.ServiceType == typeof(CliPortalSection)))
        {
            context.NeedsConfiguration("docs or public-api plugin");
            return;
        }
        context.Builder.AddPortal(o =>
        {
            if (settings.Text("path") is { Length: > 0 } path) o.Path = path;
            if (settings.Text("name") is { Length: > 0 } name) o.Name = name;
            if (settings.Text("logoUrl") is { Length: > 0 } logo) o.LogoUrl = logo;
            if (context.Options["siteDir"]?.ToString() is { Length: > 0 } site) o.SitePath = site;
        });
        context.Services.AddTransient<IStartupFilter, PortalStartupFilter>();
    }

    private sealed class PortalStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseQodalisPortal();
            next(app);
        };
    }
}
