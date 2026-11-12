using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Abstractions.Logs;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Auth;
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

        // Module registry — will be resolved after all modules are registered
        builder.Services.AddSingleton<ModuleRegistry>(sp =>
        {
            // Read modules from the CliBuilder that was captured during configuration
            return new ModuleRegistry(builder.Modules, () => sp.GetServices<ICliCommandProcessor>());
        });
        builder.Services.AddSingleton<ICliProcessorFilter>(sp => sp.GetRequiredService<ModuleRegistry>());

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
