using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions;
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
        builder.Services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<AdminConfig>>();
            var config = new AdminConfig(logger);
            configure?.Invoke(config);
            config.ResolveFromEnvironment();
            return config;
        });
        builder.Services.AddSingleton<JwtService>();

        // Register the log ring buffer and wire it into the ASP.NET logging pipeline
        var logBuffer = new LogRingBuffer();
        builder.Services.AddSingleton(logBuffer);
        builder.Services.AddLogging(logging => logging.AddProvider(logBuffer));

        // Module registry — will be resolved after all modules are registered
        builder.Services.AddSingleton<ModuleRegistry>(sp =>
        {
            // Read modules from the CliBuilder that was captured during configuration
            return new ModuleRegistry(builder.Modules);
        });
        builder.Services.AddSingleton<ICliProcessorFilter>(sp => sp.GetRequiredService<ModuleRegistry>());

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
            catch(Exception e)
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
