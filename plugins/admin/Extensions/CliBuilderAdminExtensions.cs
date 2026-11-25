using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Abstractions.Logs;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Auth;
using Qodalis.Cli.Plugin.Admin.Commands;
using Qodalis.Cli.Plugin.Admin.Controllers;
using Qodalis.Cli.Plugin.Admin.Services;

namespace Qodalis.Cli.Plugin.Admin.Extensions;

public static class CliBuilderAdminExtensions
{
    /// <summary>Path of the dashboard's color scheme document.</summary>
    public const string AdminThemePath = "/qcli/admin/theme.json";

    /// <summary>
    /// Registers admin dashboard services, controllers, and log buffer into the DI container.
    /// </summary>
    public static CliBuilder AddAdmin(this CliBuilder builder, Action<AdminConfig>? configure = null)
    {
        builder.AddInstalledPlugin(typeof(AdminConfig).Assembly);
        builder.Services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<AdminConfig>>();
            var config = new AdminConfig(logger);
            configure?.Invoke(config);
            config.Validate();
            config.ResolveFromEnvironment();
            return config;
        });
        builder.Services.AddSingleton<JwtService>();
        builder.Services.AddSingleton<AdminAuthMode>();

        // Register the log ring buffer and wire it into the ASP.NET logging pipeline; it is the Logs page's history
        // unless another store (e.g. Qodalis.Cli.Plugin.Logs.Seq) was set.
        var logBuffer = new LogRingBuffer();
        builder.Services.AddSingleton(logBuffer);
        builder.Services.TryAddSingleton<ILogStore>(logBuffer);
        builder.Services.AddLogging(logging => logging.AddProvider(logBuffer));
        // Every entry is also a log:entry event (audience logs:read): the dashboard's Logs page follows them live
        builder.Services.AddHostedService(sp => new LogEntryEvents(logBuffer, sp.GetRequiredService<ICliEventBus>()));

        // Samples the server status every 15 seconds for the dashboard's charts (GET /api/v1/qcli/status/history).
        builder.Services.TryAddSingleton<StatusHistory>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<StatusHistory>());
        builder.Services.TryAddSingleton<ServerStatus>();

        // The admin command (status, plugins, clients, logs): admin:access, logs:read for logs; the module describes it
        builder.AddModule(new AdminModule());
        builder.AddProcessor<CliAdminCommandProcessor>();

        // Module registry — will be resolved after all modules are registered
        builder.Services.AddSingleton<ModuleRegistry>(sp =>
        {
            // Read modules from the CliBuilder that was captured during configuration
            return new ModuleRegistry(builder.Modules, () => sp.GetServices<ICliCommandProcessor>());
        });
        builder.Services.AddSingleton<ICliProcessorFilter>(sp => sp.GetRequiredService<ModuleRegistry>());

        // Config: the auth (read-only) and logs providers; the API at /api/v1/qcli/config serves every provider
        builder.AddConfigProvider(sp => AdminConfigProviders.Auth(sp.GetRequiredService<AdminConfig>(), () =>
        {
            var external = sp.GetRequiredService<Qodalis.Cli.Security.CliServerOptions>().ExternalAuthentication;
            return external ? "external" : sp.GetService<Qodalis.Cli.Security.ICliAuthBridge>() != null ? "users" : "password";
        }));
        builder.AddConfigProvider(sp => AdminConfigProviders.Logs(logBuffer).SetStore(sp.GetRequiredService<AdminConfig>().LogsConfigStore));
        builder.AddRoutePermission("/api/v1/qcli/config", CliConfigPermissions.Read, CliConfigPermissions.Write);

        // Dashboard plugins from the marketplace: pages added and removed at runtime, loaded from installed.json at
        // startup, their files served at /qcli/modules/<id>/; changes need dashboard:install. Their settings are the
        // dashboard-plugins config provider, registered at startup.
        builder.Services.AddSingleton(sp => new DashboardMarketplace(
            sp.GetRequiredService<AdminConfig>().Marketplace,
            sp.GetService<IHostEnvironment>()?.ContentRootPath ?? Directory.GetCurrentDirectory(),
            sp.GetRequiredService<Qodalis.Cli.Models.CliDashboardExtensions>(),
            () => sp.GetService<Qodalis.Cli.Models.CliInstalledPlugins>()?.All ?? [],
            () => sp.GetService<IAuditStore>(),
            () => sp.GetService<ICliEventBus>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("Qodalis.Cli.Plugin.Admin"),
            sp.GetService<TimeProvider>()));
        builder.Services.AddHostedService(sp => new DashboardMarketplaceStartup(sp.GetRequiredService<DashboardMarketplace>(),
            sp.GetService<Qodalis.Cli.Config.CliConfigRegistry>()));
        builder.Services.AddTransient<IStartupFilter, DashboardMarketplaceAssets>();
        builder.AddRoutePermission("/api/v1/qcli/dashboard/marketplace", Qodalis.Cli.Security.CliRoutePolicy.AdminAccess,
            Qodalis.Cli.Security.CliRoutePolicy.DashboardInstall);

        // Server plugins from the marketplace (the same marketplace options): NuGet packages installed into
        // <data dir>/server-plugins, loaded at the next start; changes need plugins:install
        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<AdminConfig>().Marketplace;
            var plugins = sp.GetService<Qodalis.Cli.Plugins.CliServerPlugins>();
            if (!options.Enabled || plugins == null) return null!;
            return new ServerPluginMarketplace(options, plugins,
                () => sp.GetService<Qodalis.Cli.Models.CliInstalledPlugins>()?.All ?? [],
                () => sp.GetService<Qodalis.Cli.Abstractions.Restart.ICliServerRestart>(),
                () => sp.GetService<IAuditStore>(),
                () => sp.GetService<ICliEventBus>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger("Qodalis.Cli.Plugin.Admin"),
                sp.GetService<TimeProvider>());
        });
        builder.AddRoutePermission(ServerPluginMarketplace.ApiPath, Qodalis.Cli.Security.CliRoutePolicy.AdminAccess,
            Qodalis.Cli.Security.CliRoutePolicy.PluginsInstall);

        // Register the admin plugin controller assembly
        builder.AddApplicationPart(typeof(StatusController).Assembly);

        return builder;
    }

    /// <summary>
    /// The <c>theme.json</c> document: the color scheme, plus <c>branding</c> (name, icon, terminal) when the host
    /// set any. Throws <see cref="ArgumentException"/> on an invalid value.
    /// </summary>
    internal static string AdminAppearance(AdminConfig config)
    {
        var document = (config.Theme ?? new AdminTheme()).ToDocument();
        if ((config.Branding ?? new AdminBranding()).ToDocument() is { } branding)
            document["branding"] = branding;
        return System.Text.Json.JsonSerializer.Serialize(document);
    }

    /// <summary>
    /// Adds the admin auth middleware and serves the admin SPA at /qcli/admin.
    /// Call this after UseWebSockets() and before MapControllers().
    /// </summary>
    public static IApplicationBuilder UseQodalisAdmin(this IApplicationBuilder app)
    {
        // Without CLI routes the dashboard's API is gone: serve nothing rather than a dashboard that cannot work.
        if (app.ApplicationServices.GetService<Qodalis.Cli.Security.CliServerOptions>()?.CliRoutes == false)
        {
            app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Qodalis.Cli.Plugin.Admin")
                .LogWarning("[qcli] the CLI routes are turned off (WithoutCliRoutes); the admin dashboard is not served");
            return app;
        }

        var jwtService = app.ApplicationServices.GetRequiredService<JwtService>();
        app.UseMiddleware<AdminAuthMiddleware>(jwtService);

        var config = app.ApplicationServices.GetRequiredService<AdminConfig>();

        // Color scheme, read by the dashboard before its first render. Validated
        // here so a bad value fails at startup. Public, like the dashboard itself:
        // the login page uses it too.
        var themeJson = AdminAppearance(config);
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method)
                && string.Equals(context.Request.Path.Value, AdminThemePath, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.Headers.CacheControl = "no-cache";
                await context.Response.WriteAsync(themeJson);
                return;
            }
            await next();
        });

        // Resolve dashboard file provider: physical path first, then embedded resources
        var env = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
        var dashboardDir = DashboardResolver.Resolve(config.DashboardPath, env.ContentRootPath);

        IFileProvider? fileProvider = null;
        if (dashboardDir != null)
        {
            fileProvider = new PhysicalFileProvider(dashboardDir);
        }
        else
        {
            // Try embedded resources from this assembly
            try
            {
                var embeddedProvider = new ManifestEmbeddedFileProvider(
                    typeof(CliBuilderAdminExtensions).Assembly,
                    "wwwroot/admin");
                // Verify the embedded files exist
                if (embeddedProvider.GetFileInfo("index.html").Exists)
                {
                    fileProvider = embeddedProvider;
                }
            }
            catch (Exception e)
            {
                // No embedded manifest — dashboard not available
            }
        }

        if (fileProvider != null)
        {
            // SPA fallback: serve index.html directly for non-file routes under /qcli/admin
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/qcli/admin"))
                {
                    var filePath = context.Request.Path.Value?.Replace("/qcli/admin", "").TrimStart('/') ?? "";
                    var fullPath = string.IsNullOrEmpty(filePath) ? null : fileProvider.GetFileInfo(filePath);

                    if (string.IsNullOrEmpty(filePath) || fullPath == null || !fullPath.Exists)
                    {
                        // SPA fallback: serve index.html for non-file routes
                        var indexFile = fileProvider.GetFileInfo("index.html");
                        if (indexFile.Exists)
                        {
                            context.Response.ContentType = "text/html";
                            await using var stream = indexFile.CreateReadStream();
                            await stream.CopyToAsync(context.Response.Body);
                            return;
                        }
                    }
                }

                await next();
            });

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = fileProvider,
                RequestPath = "/qcli/admin",
            });
        }

        return app;
    }
}
