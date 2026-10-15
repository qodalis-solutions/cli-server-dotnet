using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.Users.Extensions;

namespace Qodalis.Cli.Tests;

/// <summary>
/// The users and admin plugins are registered together in the demo host. Their auth endpoints
/// must not collide (admin: /api/v1/qcli/auth/login, users: POST /api/v1/qcli/sessions), and with
/// the users plugin installed the dashboard authenticates through it (B10, admin:access).
/// </summary>
public class UsersAdminCoexistenceTests : IAsyncLifetime
{
    private const string AdminUser = "dashadmin";
    private const string AdminPass = "dashpass";
    private const string RootPass = "rootpass";

    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddCors();
                    services
                        .AddControllers()
                        .AddCli(cli =>
                        {
                            cli.AddUsers(o => o.DefaultAdminPassword = RootPass);
                            cli.AddAdmin(o =>
                            {
                                o.Username = AdminUser;
                                o.Password = AdminPass;
                                o.JwtSecret = "coexistence-test-secret-that-is-long-enough-for-hmac";
                            });
                        })
                        .AddJsonOptions(options =>
                        {
                            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                        });
                });
                webHost.Configure(app =>
                {
                    // Same middleware order as demo/Program.cs
                    app.UseRouting();
                    app.UseCors();
                    app.UseQodalisUsers();
                    app.UseQodalisAdmin();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                    app.UseCli();
                });
            });

        _host = await builder.StartAsync();
        _client = _host.GetTestClient();

    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task UsersLogin_ViaSessions_Returns201()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/qcli/sessions",
            new { username = "root", password = RootPass });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
        Assert.Equal("root", body.GetProperty("session").GetProperty("user").GetProperty("name").GetString());
    }

    [Fact]
    public async Task AdminLogin_GoesThroughUsersPlugin()
    {
        // B10: with the users plugin installed, the dashboard's static credentials are not used.
        var staticCreds = await _client.PostAsJsonAsync("/api/v1/qcli/auth/login",
            new { username = AdminUser, password = AdminPass });
        Assert.Equal(HttpStatusCode.Unauthorized, staticCreds.StatusCode);

        var res = await _client.PostAsJsonAsync("/api/v1/qcli/auth/login",
            new { username = "root", password = RootPass });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        Assert.Equal("root", body.GetProperty("username").GetString());

        // The token is a users session token: it works for the dashboard API and users routes.
        var status = new HttpRequestMessage(HttpMethod.Get, "/api/v1/qcli/status");
        status.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(status)).StatusCode);

        var me = new HttpRequestMessage(HttpMethod.Get, "/api/v1/qcli/auth/me");
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var meRes = await _client.SendAsync(me);
        Assert.Equal(HttpStatusCode.OK, meRes.StatusCode);
        Assert.Equal("root", (await meRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("username").GetString());

        var current = new HttpRequestMessage(HttpMethod.Get, "/api/v1/qcli/sessions/current");
        current.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(current)).StatusCode);
    }

    [Fact]
    public async Task AdminLogin_RequiresAdminAccessPermission()
    {
        var rootToken = (await (await _client.PostAsJsonAsync("/api/v1/qcli/sessions",
            new { username = "root", password = RootPass })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/qcli/users")
        {
            Content = JsonContent.Create(new { name = "plain", email = "plain@test.com", password = "plainpass" }),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rootToken);
        Assert.Equal(HttpStatusCode.Created, (await _client.SendAsync(create)).StatusCode);

        var res = await _client.PostAsJsonAsync("/api/v1/qcli/auth/login",
            new { username = "plain", password = "plainpass" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        // A users token without admin:access cannot use the dashboard API either.
        var plainToken = (await (await _client.PostAsJsonAsync("/api/v1/qcli/sessions",
            new { username = "plain", password = "plainpass" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var status = new HttpRequestMessage(HttpMethod.Get, "/api/v1/qcli/status");
        status.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(status)).StatusCode);

        // Only the exact login path is anonymous.
        var lookalike = await _client.PostAsJsonAsync("/api/v1/qcli/status/auth/login", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, lookalike.StatusCode);
    }

    [Fact]
    public async Task AdminLogin_RejectsOversizedCredentials_LikeUsersLogin()
    {
        // Same input limits as POST /sessions: an unbounded attempted name would otherwise be
        // kept as a lockout key and audited.
        var longName = await _client.PostAsJsonAsync("/api/v1/qcli/auth/login",
            new { username = new string('a', 300), password = "whatever" });
        Assert.Equal(HttpStatusCode.BadRequest, longName.StatusCode);

        var longPassword = await _client.PostAsJsonAsync("/api/v1/qcli/auth/login",
            new { username = "root", password = new string('p', 2000) });
        Assert.Equal(HttpStatusCode.BadRequest, longPassword.StatusCode);
    }

    [Fact]
    public async Task UsersToken_AcceptedByUsersEndpoints()
    {
        var login = await _client.PostAsJsonAsync("/api/v1/qcli/sessions",
            new { username = "root", password = RootPass });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/qcli/sessions/current");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("root", body.GetProperty("user").GetProperty("name").GetString());
    }
}
