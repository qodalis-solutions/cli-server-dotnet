using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Config;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Extensions;

namespace Qodalis.Cli.Tests.Config;

/// <summary>A CLI server without authentication, with CORS (and optionally the admin plugin) for the config tests.</summary>
internal sealed class ConfigTestHost : IAsyncDisposable
{
    public static readonly CliConfigContext All = CliConfigContext.For(null, "api");

    private ConfigTestHost(IHost host)
    {
        Host = host;
        Client = host.GetTestClient();
    }

    public IHost Host { get; }
    public HttpClient Client { get; }

    public CliConfigService Config => Host.Services.GetRequiredService<CliConfigService>();
    public CliConfigRegistry Registry => Host.Services.GetRequiredService<CliConfigRegistry>();
    public T Service<T>() where T : notnull => Host.Services.GetRequiredService<T>();

    public static async Task<ConfigTestHost> StartAsync(Action<CliBuilder>? configure = null, bool admin = false)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(s => s.AddControllers().AddCli(cli =>
            {
                configure?.Invoke(cli);
                if (admin) cli.AddAdmin();
            }));
            web.Configure(app =>
            {
                app.UseWebSockets();
                app.UseRouting();
                app.UseCors();
                if (admin) app.UseQodalisAdmin();
                app.UseEndpoints(e => e.MapControllers());
                app.UseCli();
            });
        });
        var host = new ConfigTestHost(await builder.StartAsync());
        await host.Config.ReadyAsync();
        return host;
    }

    /// <summary>Signs in to the admin dashboard (password mode) and sends its token from now on.</summary>
    public async Task SignInAdminAsync()
    {
        var res = await Client.PostAsJsonAsync("/api/v1/qcli/auth/login", new { username = "admin", password = "admin" });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<HttpResponseMessage> GetWithOriginAsync(string path, string origin)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("Origin", origin);
        return await Client.SendAsync(req);
    }

    public static string? AllowOrigin(HttpResponseMessage res) =>
        res.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.FirstOrDefault() : null;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.StopAsync();
        Host.Dispose();
    }
}
