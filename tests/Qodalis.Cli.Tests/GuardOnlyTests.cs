using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Plugin.Users;
using Qodalis.Cli.Plugin.Users.Authorization;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Security;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// C10: a second service that only validates tokens (<c>WithoutCliRoutes</c> + <c>UseGuardOnly</c>) over the stores of
/// the service that owns the users.
/// </summary>
public class GuardOnlyTests : IAsyncLifetime
{
    private readonly InMemoryUserStore _users = new();
    private readonly InMemoryGroupStore _groups = new();
    private readonly InMemoryApiTokenStore _tokens = new();
    private UsersTestHost _main = null!;
    private string _hostToken = null!;

    private static void Rates(CliBuilder cli) =>
        cli.AddPermissions(p => p.Group("Rates").Add("rates:read", "Official exchange rates"));

    public async Task InitializeAsync()
    {
        _main = await UsersTestHost.StartAsync(o =>
        {
            o.UserStore = _users;
            o.GroupStore = _groups;
            o.ApiTokenStore = _tokens;
        }, configureCli: Rates);
        var root = await _main.RootTokenAsync();
        await _main.CreateGroupAsync(root, "alice-group", "rates:read");
        var aliceId = (await _main.CreateUserAsync(root, "alice", groups: ["alice-group"])).GetProperty("id").GetString()!;
        _hostToken = (await _main.Service<IApiTokenManager>().CreateAsync(aliceId, new ApiTokenCreateOptions
        {
            Name = "site", Audience = ApiTokenAudience.Host, Permissions = ["rates:read"],
        })).Token;
    }

    public async Task DisposeAsync() => await _main.DisposeAsync();

    /// <summary>The guard-only service: no CLI routes, users plugin in guard-only mode, one guarded host route.</summary>
    private static async Task<IHost> StartGuardOnlyAsync(IUserStore users, IGroupStore groups, IApiTokenStore tokens,
        bool useCli = false, bool cliRoutes = false, Action<CliBuilder>? extraCli = null)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services => services.AddControllers().AddCli(cli =>
            {
                if (!cliRoutes) cli.WithoutCliRoutes();
                Rates(cli);
                extraCli?.Invoke(cli);
                cli.AddUsers(o =>
                {
                    o.UseGuardOnly();
                    o.SetUserStore(_ => users);
                    o.SetGroupStore(_ => groups);
                    o.SetApiTokenStore(_ => tokens);
                });
            }));
            web.Configure(app =>
            {
                app.UseWebSockets();
                app.UseRouting();
                app.UseQodalisUsers();
                app.UseEndpoints(e =>
                {
                    e.MapControllers();
                    e.MapGet("/host/rates", (HttpContext ctx) => ctx.GetQcliCaller()!.UserName)
                        .RequireQcliAuthorization("rates:read", hostTokenOnly: true);
                });
                if (useCli) app.UseCli();
            });
        });
        return await builder.StartAsync();
    }

    private static Task<HttpResponseMessage> SendAsync(IHost host, HttpMethod method, string path, string? token = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (method == HttpMethod.Post) req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        return host.GetTestClient().SendAsync(req);
    }

    [Fact]
    public async Task HostRoute_AcceptsHostToken()
    {
        using var guard = await StartGuardOnlyAsync(_users, _groups, _tokens);

        var res = await SendAsync(guard, HttpMethod.Get, "/host/rates", _hostToken);

        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal("alice", await res.Content.ReadAsStringAsync());
        await guard.StopAsync();
    }

    [Fact]
    public async Task HostRoute_RefusesAnonymousAndUnknownTokens()
    {
        using var guard = await StartGuardOnlyAsync(_users, _groups, _tokens);

        Assert.Equal("unauthorized", await UsersTestHost.AssertErrorAsync(await SendAsync(guard, HttpMethod.Get, "/host/rates"), 401));
        Assert.Equal("invalid or expired token", await UsersTestHost.AssertErrorAsync(
            await SendAsync(guard, HttpMethod.Get, "/host/rates", "qcli_pat_unknown"), 401));
        await guard.StopAsync();
    }

    [Theory]
    [InlineData("GET", "/api/v1/qcli/commands")]
    [InlineData("POST", "/api/v1/qcli/execute")]
    [InlineData("POST", "/api/v1/qcli/auth/login")]
    [InlineData("POST", "/api/v1/qcli/sessions")]
    [InlineData("GET", "/api/v1/qcli/tokens")]
    [InlineData("GET", "/api/v1/qcli/permissions")]
    [InlineData("GET", "/api/v1/qcli/audit")]
    [InlineData("GET", "/api/v1/qcli/users")]
    [InlineData("GET", "/api/qcli/versions")]
    [InlineData("GET", "/ws/v1/qcli/events")]
    public async Task CliAndUsersRoutes_Answer404(string method, string path)
    {
        using var guard = await StartGuardOnlyAsync(_users, _groups, _tokens);

        Assert.Equal(404, (int)(await SendAsync(guard, new HttpMethod(method), path)).StatusCode);
        Assert.Equal(404, (int)(await SendAsync(guard, new HttpMethod(method), path, _hostToken)).StatusCode);
        await guard.StopAsync();
    }

    [Fact]
    public async Task NoSeeding()
    {
        var users = new InMemoryUserStore();
        var groups = new InMemoryGroupStore();
        using var guard = await StartGuardOnlyAsync(users, groups, new InMemoryApiTokenStore());

        await SendAsync(guard, HttpMethod.Get, "/host/rates", "qcli_pat_unknown");

        Assert.Empty(await users.GetUsersAsync());
        Assert.Empty(await groups.GetGroupsAsync());
        await guard.StopAsync();
    }

    [Fact]
    public async Task UseCli_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartGuardOnlyAsync(_users, _groups, _tokens, useCli: true));

        Assert.Equal("UseCli() is not available: the CLI routes are turned off (WithoutCliRoutes)", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardOnlyWithCliRoutes_FailsAtStartup(bool withoutCliRoutesAfterUsers)
    {
        // CLI routes on: never served unprotected. Also when WithoutCliRoutes() would come after AddUsers().
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services => services.AddControllers().AddCli(cli =>
            {
                cli.AddUsers(o => o.UseGuardOnly());
                if (withoutCliRoutesAfterUsers) cli.WithoutCliRoutes();
            }));
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseQodalisUsers();
                app.UseEndpoints(e => e.MapControllers());
            });
        });

        if (withoutCliRoutesAfterUsers)
        {
            using var host = await builder.StartAsync();
            Assert.False(host.Services.GetRequiredService<CliServerOptions>().CliRoutes);
            await host.StopAsync();
            return;
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.StartAsync());
        Assert.Equal("guard-only needs WithoutCliRoutes()", ex.Message);
    }

    [Fact]
    public async Task GuardOnlyWithCliRoutes_FailsAtStartup_WithoutUseQodalisUsers()
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services => services.AddControllers().AddCli(cli => cli.AddUsers(o => o.UseGuardOnly())));
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(e => e.MapControllers());
                app.UseCli();
            });
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.StartAsync());
        Assert.Equal("guard-only needs WithoutCliRoutes()", ex.Message);
    }

    [Theory]
    [InlineData("POST", "/api/v1/qcli/execute")]
    [InlineData("POST", "/api/v1/qcli/execute/stream")]
    [InlineData("POST", "/api/qcli/mcp")]
    [InlineData("GET", "/api/qcli/data-explorer/sources")]
    [InlineData("GET", "/api/qcli/fs/ls")]
    [InlineData("GET", "/api/v1/qcli/jobs")]
    [InlineData("POST", "/api/v1/qcli/sessions")]
    [InlineData("POST", "/api/v1/qcli/auth/login")]
    [InlineData("GET", "/api/v1/qcli/auth/mode")]
    [InlineData("POST", "/api/v1/qcli/link")]
    [InlineData("GET", "/api/v1/qcli/users")]
    [InlineData("GET", "/api/v1/qcli/groups")]
    [InlineData("GET", "/ws/v1/qcli/shell")]
    [InlineData("GET", "/ws/v1/qcli/logs")]
    [InlineData("GET", "/ws/qcli/events")]
    public async Task GuardOnlyWithoutCliRoutes_EverythingElseIs404(string method, string path)
    {
        using var guard = await StartGuardOnlyAsync(_users, _groups, _tokens, extraCli: cli =>
        {
            cli.AddMcp();
            cli.EnableShell(o => o.AllowAnonymous = true);
            cli.AddFileSystem(o => o.UseInMemory());
        });

        Assert.Equal(404, (int)(await SendAsync(guard, new HttpMethod(method), path)).StatusCode);
        Assert.Equal(404, (int)(await SendAsync(guard, new HttpMethod(method), path, _hostToken)).StatusCode);
        Assert.Equal(404, (int)(await guard.GetTestServer().CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws/v1/qcli/events"), CancellationToken.None)
            .ContinueWith(t => t.IsFaulted && t.Exception!.InnerException!.Message.Contains("404") ? 404 : 0)));
        await guard.StopAsync();
    }

    [Fact]
    public async Task Catalog_StillServesHostEntries()
    {
        using var guard = await StartGuardOnlyAsync(_users, _groups, _tokens);

        Assert.NotNull(guard.Services.GetRequiredService<ICliPermissionCatalog>().Find("rates:read"));
        Assert.False(guard.Services.GetRequiredService<CliServerOptions>().CliRoutes);
        Assert.False(guard.Services.GetRequiredService<CliServerOptions>().RequireAuthentication);
        await guard.StopAsync();
    }

    [Fact]
    public async Task DefaultHost_Unchanged()
    {
        Assert.True(_main.Service<CliServerOptions>().CliRoutes);
        Assert.False(_main.Service<UsersModuleOptions>().GuardOnly);
        Assert.Equal(200, (int)(await _main.GetAsync("/api/v1/qcli/commands", await _main.RootTokenAsync())).StatusCode);
        Assert.Equal(200, (int)(await _main.GetAsync("/api/qcli/versions")).StatusCode);
    }
}
