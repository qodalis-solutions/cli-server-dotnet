using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.Docs;
using Qodalis.Cli.Plugin.PublicApi;
using Qodalis.Cli.Plugin.Users.Authorization;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>A host's own controller, discovered by MVC with the plugins' controllers.</summary>
[ApiController]
[Route("host/ping")]
public sealed class HostPingController : ControllerBase
{
    [HttpGet]
    public string Get() => "pong";
}

/// <summary>
/// A real host: MVC discovers controllers in every assembly of the application's dependency graph (the application
/// name is this assembly, which references core and every plugin), not only in the parts <c>AddCli</c> adds. With
/// <c>WithoutCliRoutes()</c> none of the CLI's controllers may be routable; without it nothing changes.
/// </summary>
public class CliRoutesDiscoveryTests : IAsyncLifetime
{
    private readonly InMemoryUserStore _users = new();
    private readonly InMemoryGroupStore _groups = new();
    private readonly InMemoryApiTokenStore _tokens = new();
    private UsersTestHost _main = null!;
    private string _rootSession = null!;
    private string _rootToken = null!;
    private string _cliToken = null!;
    private string _hostToken = null!;
    private string _site = null!;

    private static void Rates(CliBuilder cli) =>
        cli.AddPermissions(p => p.Group("Rates").Add("rates:read", "Official exchange rates"));

    public async Task InitializeAsync()
    {
        _site = Path.Combine(Path.GetTempPath(), $"qcli-admin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_site);
        await File.WriteAllTextAsync(Path.Combine(_site, "index.html"), "<html>admin</html>");

        _main = await UsersTestHost.StartAsync(o =>
        {
            o.UserStore = _users;
            o.GroupStore = _groups;
            o.ApiTokenStore = _tokens;
        }, configureCli: Rates);
        _rootSession = await _main.RootTokenAsync();
        await _main.CreateGroupAsync(_rootSession, "alice-group", "rates:read");
        var aliceId = (await _main.CreateUserAsync(_rootSession, "alice", groups: ["alice-group"])).GetProperty("id").GetString()!;
        var tokens = _main.Service<IApiTokenManager>();
        _hostToken = (await tokens.CreateAsync(aliceId, new ApiTokenCreateOptions
        {
            Name = "site", Audience = ApiTokenAudience.Host, Permissions = ["rates:read"],
        })).Token;
        var rootId = (await _users.GetUsersAsync()).Single(u => u.Name == "root").Id;
        // Sessions live in the process that created them; tokens are in the shared store.
        _rootToken = (await tokens.CreateAsync(rootId, new ApiTokenCreateOptions { Name = "root terminal" })).Token;
        _cliToken = (await tokens.CreateAsync(aliceId, new ApiTokenCreateOptions
        {
            Name = "terminal", Audience = ApiTokenAudience.Cli, Permissions = ["rates:read"],
        })).Token;
    }

    public async Task DisposeAsync()
    {
        await _main.DisposeAsync();
        Directory.Delete(_site, true);
    }

    private async Task<IHost> StartAsync(bool cliRoutes)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddOpenApi("public");
                services.AddControllers()
                    .AddApplicationPart(typeof(HostPingController).Assembly) // the host's own controllers
                    .AddCli(cli =>
                {
                    if (!cliRoutes) cli.WithoutCliRoutes();
                    Rates(cli);
                    cli.AddUsers(o =>
                    {
                        if (!cliRoutes) o.UseGuardOnly();
                        o.DefaultAdminPassword = "root";
                        o.SetUserStore(_ => _users);
                        o.SetGroupStore(_ => _groups);
                        o.SetApiTokenStore(_ => _tokens);
                    });
                    cli.AddAdmin(o => o.DashboardPath = _site);
                    cli.AddPublicApi();
                    cli.AddDocs(o => o.AddPage("index", "# Welcome"));
                });
            });
            web.Configure(app =>
            {
                app.UseWebSockets();
                app.UseRouting();
                app.UseQodalisUsers();
                app.UseQodalisAdmin();
                app.UseQodalisPublicApi();
                app.UseQodalisDocs();
                app.UseEndpoints(e =>
                {
                    e.MapControllers();
                    e.MapGet("/host/rates", (HttpContext ctx) => ctx.GetQcliCaller()!.UserName)
                        .RequirePublicApi("rates:read");
                });
                if (cliRoutes) app.UseCli();
            });
            // As in a real app: the application is the demo, a Web SDK project whose build lists every referenced
            // assembly that uses MVC ([assembly: ApplicationPart("Qodalis.Cli")], "...Plugin.Users", "...Admin", ...);
            // MVC adds those as application parts on its own, whatever AddCli registers. (After Configure, which
            // names the application after the assembly of its delegate.)
            web.UseSetting(WebHostDefaults.ApplicationKey, "Demo");
        });
        return await builder.StartAsync();
    }

    private static Task<HttpResponseMessage> SendAsync(IHost host, string method, string path, string? token = null)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (method is "POST" or "PUT") req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        return host.GetTestClient().SendAsync(req);
    }

    public static TheoryData<string, string> CliRoutes => new()
    {
        { "GET", "/api/v1/qcli/version" },
        { "GET", "/api/v1/qcli/capabilities" },
        { "GET", "/api/v1/qcli/commands" },
        { "GET", "/api/v1/qcli/commands/stats" },
        { "POST", "/api/v1/qcli/execute" },
        { "POST", "/api/v1/qcli/execute/stream" },
        { "GET", "/api/qcli/versions" },
        { "GET", "/api/qcli/fs/ls?path=/" },
        { "GET", "/api/qcli/data-explorer/sources" },
        { "POST", "/api/qcli/mcp" },
        { "GET", "/api/qcli/mcp" },
        { "GET", "/api/v1/qcli/status" },
        { "GET", "/api/v1/qcli/plugins" },
        { "GET", "/api/v1/qcli/config" },
        { "GET", "/api/v1/qcli/logs" },
        { "GET", "/api/v1/qcli/ws/clients" },
        { "POST", "/api/v1/qcli/auth/login" },
        { "GET", "/api/v1/qcli/audit" },
        { "GET", "/api/v1/qcli/jobs" },
        { "GET", "/api/v1/qcli/users" },
        { "GET", "/api/v1/qcli/groups" },
        { "GET", "/api/v1/qcli/tokens" },
        { "GET", "/api/v1/qcli/permissions" },
        { "POST", "/api/v1/qcli/sessions" },
        { "POST", "/api/v1/qcli/sessions/ws-ticket" },
        { "GET", "/api/v1/qcli/link" },
        { "GET", "/qcli/admin" },
        { "GET", "/qcli/admin/" },
        { "GET", "/ws/v1/qcli/events" },
        { "GET", "/ws/qcli/events" },
        { "GET", "/ws/v1/qcli/shell" },
        { "GET", "/ws/v1/qcli/logs" },
    };

    [Theory]
    [MemberData(nameof(CliRoutes))]
    public async Task WithoutCliRoutes_DiscoveredCliRoutes_Answer404(string method, string path)
    {
        using var host = await StartAsync(cliRoutes: false);

        foreach (var token in new[] { null, _rootSession, _rootToken, _cliToken, _hostToken })
        {
            var res = await SendAsync(host, method, path, token);
            Assert.True(404 == (int)res.StatusCode, $"{method} {path} ({token ?? "anonymous"}): {(int)res.StatusCode}");
        }
        await host.StopAsync();
    }

    [Theory]
    [InlineData("/ws/v1/qcli/events")]
    [InlineData("/ws/qcli/shell")]
    [InlineData("/ws/v1/qcli/logs")]
    public async Task WithoutCliRoutes_SocketUpgrades_Answer404(string path)
    {
        using var host = await StartAsync(cliRoutes: false);
        var client = host.GetTestServer().CreateWebSocketClient();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.ConnectAsync(new Uri($"ws://localhost{path}?token={_rootToken}"), CancellationToken.None));
        Assert.Contains("404", ex.Message);
        await host.StopAsync();
    }

    [Fact]
    public async Task WithoutCliRoutes_HostRoutesAndPluginEndpointsStay()
    {
        using var host = await StartAsync(cliRoutes: false);

        var ping = await SendAsync(host, "GET", "/host/ping");
        Assert.Equal(200, (int)ping.StatusCode);
        Assert.Equal("pong", await ping.Content.ReadAsStringAsync());
        var rates = await SendAsync(host, "GET", "/host/rates", _hostToken);
        Assert.Equal(200, (int)rates.StatusCode);
        Assert.Equal("alice", await rates.Content.ReadAsStringAsync());
        Assert.Equal(200, (int)(await SendAsync(host, "GET", "/qcli/api/openapi.json")).StatusCode);
        Assert.Equal(200, (int)(await SendAsync(host, "GET", "/qcli/api/swagger/")).StatusCode);
        Assert.Equal(200, (int)(await SendAsync(host, "GET", "/qcli/docs/pages")).StatusCode);
        await host.StopAsync();
    }

    [Fact]
    public async Task WithCliRoutes_EverythingAsBefore()
    {
        using var host = await StartAsync(cliRoutes: true);

        Assert.Equal(200, (int)(await SendAsync(host, "GET", "/api/v1/qcli/commands", _rootToken)).StatusCode);
        Assert.Equal(200, (int)(await SendAsync(host, "GET", "/api/qcli/versions")).StatusCode);
        Assert.Equal(200, (int)(await SendAsync(host, "GET", "/api/v1/qcli/users", _rootToken)).StatusCode);
        Assert.Equal(200, (int)(await SendAsync(host, "GET", "/api/v1/qcli/status", _rootToken)).StatusCode);
        Assert.Equal(401, (int)(await SendAsync(host, "GET", "/api/v1/qcli/commands")).StatusCode);
        Assert.Equal(200, (int)(await SendAsync(host, "GET", "/qcli/admin/")).StatusCode);
        Assert.Equal(200, (int)(await SendAsync(host, "GET", "/host/ping")).StatusCode);
        Assert.Equal(200, (int)(await SendAsync(host, "GET", "/host/rates", _hostToken)).StatusCode);
        Assert.Equal(200, (int)(await SendAsync(host, "GET", "/qcli/api/openapi.json")).StatusCode);
        Assert.Equal(200, (int)(await SendAsync(host, "GET", "/qcli/docs/pages")).StatusCode);
        // A host token is still refused on the CLI routes.
        Assert.Equal(403, (int)(await SendAsync(host, "GET", "/api/v1/qcli/commands", _hostToken)).StatusCode);
        await host.StopAsync();
    }
}
