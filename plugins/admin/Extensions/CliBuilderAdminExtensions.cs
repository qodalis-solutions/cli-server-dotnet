using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Auth;
using Qodalis.Cli.Plugin.Admin.Controllers;
using Qodalis.Cli.Plugin.Admin.Services;

namespace Qodalis.Cli.Plugin.Admin.Extensions;

public static class CliBuilderAdminExtensions
{
    /// <summary>
    /// Registers admin dashboard services, controllers, and log buffer into the DI container.
    /// </summary>
    public static CliBuilder AddAdmin(this CliBuilder builder, Action<AdminConfig>? configure = null)
    {
        var config = new AdminConfig();
        configure?.Invoke(config);
        config.ResolveFromEnvironment();

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<JwtService>();

        // Register the log ring buffer as both a singleton and an ILoggerProvider
        var logBuffer = new LogRingBuffer();
        builder.Services.AddSingleton(logBuffer);
        builder.Services.AddSingleton<ILoggerProvider>(logBuffer);

        // Module registry — will be resolved after all modules are registered
        builder.Services.AddSingleton<ModuleRegistry>(sp =>
        {
            // Read modules from the CliBuilder that was captured during configuration
            return new ModuleRegistry(builder.Modules);
        });

        // Register the admin plugin controller assembly
        builder.AddApplicationPart(typeof(StatusController).Assembly);

        return builder;
    }

    /// <summary>
    /// Adds the admin auth middleware and serves the admin SPA at /qcli/admin.
    /// Call this after UseWebSockets() and before MapControllers().
    /// </summary>
    public static IApplicationBuilder UseQodalisAdmin(this IApplicationBuilder app)
    {
        var jwtService = app.ApplicationServices.GetRequiredService<JwtService>();
        app.UseMiddleware<AdminAuthMiddleware>(jwtService);

        // Resolve dashboard file provider: physical path first, then embedded resources
        var config = app.ApplicationServices.GetRequiredService<AdminConfig>();
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
            catch
            {
                // No embedded manifest — dashboard not available
            }
        }

        if (fileProvider != null)
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = fileProvider,
                RequestPath = "/qcli/admin",
            });

            // SPA fallback: serve index.html for all /qcli/admin/* routes that don't match a file
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/qcli/admin"))
                {
                    var subPath = context.Request.Path.Value?.Replace("/qcli/admin", "").TrimStart('/') ?? "";
                    if (string.IsNullOrEmpty(subPath) || !fileProvider.GetFileInfo(subPath).Exists)
                    {
                        context.Request.Path = "/qcli/admin/index.html";
                    }
                }

                await next();
            });

            // Serve static files again for the rewritten path
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = fileProvider,
                RequestPath = "/qcli/admin",
            });
        }

        return app;
    }
}
