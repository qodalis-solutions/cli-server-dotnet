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

        // Resolve dashboard dist directory
        var config = app.ApplicationServices.GetRequiredService<AdminConfig>();
        var env = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
        var dashboardDir = DashboardResolver.Resolve(config.DashboardPath, env.ContentRootPath);

        if (dashboardDir != null)
        {
            var fileProvider = new PhysicalFileProvider(dashboardDir);

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
                    var filePath = context.Request.Path.Value?.Replace("/qcli/admin", "").TrimStart('/') ?? "";
                    var fullPath = Path.Combine(dashboardDir, filePath);

                    // If the path doesn't point to a real file, serve index.html
                    if (!File.Exists(fullPath) || string.IsNullOrEmpty(filePath))
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
