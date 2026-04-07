using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Tests;

/// <summary>
/// Integration tests for the external auth adapter feature.
/// Uses a custom header (X-Test-User) to simulate host-app authentication.
/// </summary>
public class ExternalAuthAdapterTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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
                            cli.AddUsers(options =>
                            {
                                options.UseExternalAuth(context =>
                                {
                                    var userHeader = context.Request.Headers["X-Test-User"].FirstOrDefault();
                                    if (string.IsNullOrEmpty(userHeader))
                                        return null;

                                    var isAdmin = context.Request.Headers["X-Test-Admin"].FirstOrDefault() == "true";

                                    return new ExternalAuthResult
                                    {
                                        User = new User
                                        {
                                            Id = $"ext-{userHeader}",
                                            Name = userHeader,
                                            Email = $"{userHeader}@external.com",
                                            Groups = isAdmin ? ["admin"] : [],
                                        },
                                        IsAdmin = isAdmin,
                                    };
                                });
                            });
                        })
                        .AddJsonOptions(options =>
                        {
                            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                            options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                        });
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseCors();
                    app.UseQodalisUsers();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapControllers();
                    });
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

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private HttpRequestMessage MakeRequest(HttpMethod method, string url, string? user = null, bool admin = false)
    {
        var req = new HttpRequestMessage(method, url);
        if (user != null)
        {
            req.Headers.Add("X-Test-User", user);
            if (admin)
                req.Headers.Add("X-Test-Admin", "true");
        }
        return req;
    }

    private async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(content, JsonOptions);
    }

    // =========================================================================
    // Auth endpoints should return 501 when adapter is present
    // =========================================================================

    [Fact]
    public async Task Login_WithExternalAdapter_Returns501()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/qcli/auth/login",
            new { username = "root", password = "root" }, JsonOptions);

        Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Equal("Authentication is managed by the host application",
            body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Logout_WithExternalAdapter_Returns501()
    {
        var req = MakeRequest(HttpMethod.Post, "/api/v1/qcli/auth/logout", "admin-user", admin: true);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Equal("Authentication is managed by the host application",
            body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Verify_WithExternalAdapter_Returns501()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/qcli/auth/verify",
            new { userId = "some-id", password = "some-pass" }, JsonOptions);

        Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Equal("Authentication is managed by the host application",
            body.GetProperty("error").GetString());
    }

    // =========================================================================
    // Adapter authenticates requests via custom header
    // =========================================================================

    [Fact]
    public async Task GetUsers_WithAuthenticatedUser_Returns200()
    {
        var req = MakeRequest(HttpMethod.Get, "/api/v1/qcli/users", "alice", admin: true);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
    }

    [Fact]
    public async Task GetUsers_WithoutAuthHeader_Returns401()
    {
        var req = MakeRequest(HttpMethod.Get, "/api/v1/qcli/users");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // =========================================================================
    // Admin-only endpoints respect IsAdmin from adapter
    // =========================================================================

    [Fact]
    public async Task CreateUser_AdminAdapter_Returns201()
    {
        var req = MakeRequest(HttpMethod.Post, "/api/v1/qcli/users", "admin-user", admin: true);
        req.Content = JsonContent.Create(
            new { name = "ext-new-user", email = "ext@test.com" }, options: JsonOptions);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Equal("ext-new-user", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task CreateUser_NonAdminAdapter_Returns403()
    {
        var req = MakeRequest(HttpMethod.Post, "/api/v1/qcli/users", "regular-user", admin: false);
        req.Content = JsonContent.Create(
            new { name = "should-fail", email = "fail@test.com" }, options: JsonOptions);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task GetGroups_AdminAdapter_Returns200()
    {
        var req = MakeRequest(HttpMethod.Get, "/api/v1/qcli/groups", "admin-user", admin: true);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task GetSessions_AdminAdapter_Returns200()
    {
        var req = MakeRequest(HttpMethod.Get, "/api/v1/qcli/sessions", "admin-user", admin: true);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task GetCurrentSession_AuthenticatedUser_Returns200()
    {
        var req = MakeRequest(HttpMethod.Get, "/api/v1/qcli/sessions/current", "bob");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Equal("bob", body.GetProperty("user").GetProperty("name").GetString());
    }

    // =========================================================================
    // Non-protected routes should pass through regardless of adapter
    // =========================================================================

    [Fact]
    public async Task NonProtectedRoute_PassesThrough()
    {
        // A route that is not under the users plugin prefixes should not be blocked
        var res = await _client.GetAsync("/api/v1/qcli/version");
        // Could be 200 or 404 depending on whether the endpoint exists,
        // but should NOT be 401
        Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
