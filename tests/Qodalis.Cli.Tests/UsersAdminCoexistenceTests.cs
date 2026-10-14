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
/// Regression: the users and admin plugins are registered together in the demo
/// host. Their auth endpoints must not collide — the admin dashboard keeps
/// /api/v1/qcli/auth/login, the users plugin logs in via POST /api/v1/qcli/sessions.
/// Before the fix every login returned 500 (AmbiguousMatchException).
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
    public async Task AdminLogin_StillAtAuth_Returns200()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/qcli/auth/login",
            new { username = AdminUser, password = AdminPass });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
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
