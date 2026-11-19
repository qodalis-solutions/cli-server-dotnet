using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>The admin dashboard is not served without CLI routes: its API is gone.</summary>
public class AdminWithoutCliRoutesTests
{
    [Fact]
    public async Task UseQodalisAdmin_WithoutCliRoutes_ServesNothing_AndWarns()
    {
        var logs = new CapturingLoggerProvider();
        var site = Path.Combine(Path.GetTempPath(), $"qcli-admin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(site);
        await File.WriteAllTextAsync(Path.Combine(site, "index.html"), "<html>admin</html>");
        try
        {
            using var host = await new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                    services.AddControllers().AddCli(cli =>
                    {
                        cli.WithoutCliRoutes();
                        cli.AddAdmin(o => o.DashboardPath = site);
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseQodalisAdmin();
                    app.UseEndpoints(e => e.MapControllers());
                });
            }).StartAsync();
            var client = host.GetTestClient();

            Assert.Equal(404, (int)(await client.GetAsync("/qcli/admin")).StatusCode);
            Assert.Equal(404, (int)(await client.GetAsync("/qcli/admin/theme.json")).StatusCode);
            Assert.Equal(404, (int)(await client.GetAsync("/api/v1/qcli/status")).StatusCode);
            Assert.Single(logs.Messages, m => m == "[qcli] the CLI routes are turned off (WithoutCliRoutes); the admin dashboard is not served");
            await host.StopAsync();
        }
        finally
        {
            Directory.Delete(site, true);
        }
    }
}
