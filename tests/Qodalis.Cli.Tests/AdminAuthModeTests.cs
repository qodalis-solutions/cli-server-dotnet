using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Plugin.Users;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Tests;

/// <summary>
/// <c>GET /api/v1/qcli/auth/mode</c> tells the dashboard which sign-in to show: its own password
/// form, or the host application's sign-in when the users plugin uses an external auth adapter.
/// </summary>
public class AdminAuthModeTests
{
    private const string AdminUser = "dashadmin";
    private const string AdminPass = "dashpass";
    private const string HostUserHeader = "X-Host-User";

    private static async Task<IHost> StartAsync(Action<UsersModuleOptions>? users, string? signInUrl = null)
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
                            if (users != null) cli.AddUsers(users);
                            cli.AddAdmin(o =>
                            {
                                o.Username = AdminUser;
                                o.Password = AdminPass;
                                o.JwtSecret = "auth-mode-test-secret-that-is-long-enough-for-hmac";
                                o.SignInUrl = signInUrl;
                            });
                        })
                        .AddJsonOptions(options =>
                            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseCors();
                    if (users != null) app.UseQodalisUsers();
                    app.UseQodalisAdmin();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                    app.UseCli();
                });
            });
        return await builder.StartAsync();
    }

    /// <summary>Host session: any request with <c>X-Host-User</c> is signed in as an admin.</summary>
    private static void ExternalAuth(UsersModuleOptions o) => o.UseExternalAuth(context =>
    {
        var name = context.Request.Headers[HostUserHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(name)) return null;
        return new ExternalAuthResult
        {
            User = new User { Id = $"ext-{name}", Name = name, Email = $"{name}@host.test" },
            IsAdmin = true,
        };
    });

    private static async Task<JsonElement> GetModeAsync(IHost host)
    {
        var res = await host.GetTestClient().GetAsync("/api/v1/qcli/auth/mode");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> LoginAsync(IHost host, string user, string pass) =>
        host.GetTestClient().PostAsJsonAsync("/api/v1/qcli/auth/login", new { username = user, password = pass });

    [Fact]
    public async Task Mode_WithoutUsersPlugin_IsPassword()
    {
        using var host = await StartAsync(users: null);
        var body = await GetModeAsync(host);
        Assert.Equal("password", body.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("signInUrl").ValueKind);
        await host.StopAsync();
    }

    [Fact]
    public async Task Mode_UsersPluginWithPasswords_IsPassword_AndPublicOnProtectedServer()
    {
        using var host = await StartAsync(o => o.DefaultAdminPassword = "rootpass", "https://app.example.com/sign-in");
        // No Authorization header: the route is public even though the users plugin protects the server.
        var body = await GetModeAsync(host);
        Assert.Equal("password", body.GetProperty("mode").GetString());
        Assert.Equal("https://app.example.com/sign-in", body.GetProperty("signInUrl").GetString());
        await host.StopAsync();
    }

    [Fact]
    public async Task Mode_ExternalAuth_IsExternal_WithSignInUrl()
    {
        using var host = await StartAsync(ExternalAuth, "/sign-in");
        var body = await GetModeAsync(host);
        Assert.Equal("external", body.GetProperty("mode").GetString());
        Assert.Equal("/sign-in", body.GetProperty("signInUrl").GetString());
        await host.StopAsync();
    }

    [Fact]
    public async Task Mode_ExternalAuth_WithoutSignInUrl_ReturnsNull()
    {
        using var host = await StartAsync(ExternalAuth);
        var body = await GetModeAsync(host);
        Assert.Equal("external", body.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("signInUrl").ValueKind);
        await host.StopAsync();
    }

    [Fact]
    public async Task Login_ExternalAuth_StaticCredentials_Return501()
    {
        using var host = await StartAsync(ExternalAuth, "https://app.example.com/sign-in");
        var res = await LoginAsync(host, AdminUser, AdminPass);
        Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("token", out _));
        Assert.Equal("EXTERNAL_AUTH", body.GetProperty("code").GetString());
        Assert.Contains("https://app.example.com/sign-in", body.GetProperty("error").GetString());
        Assert.Equal("https://app.example.com/sign-in", body.GetProperty("signInUrl").GetString());
        await host.StopAsync();
    }

    [Fact]
    public async Task Login_ExternalAuth_WithoutSignInUrl_PointsToHostApp()
    {
        using var host = await StartAsync(ExternalAuth);
        var res = await LoginAsync(host, AdminUser, AdminPass);
        Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("host application", body.GetProperty("error").GetString());
        await host.StopAsync();
    }

    [Fact]
    public async Task ExternalAuth_HostSession_ReachesAdminRoutes()
    {
        using var host = await StartAsync(ExternalAuth);
        var client = host.GetTestClient();

        var anonymous = await client.GetAsync("/api/v1/qcli/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/qcli/auth/me");
        req.Headers.Add(HostUserHeader, "alice");
        var me = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("alice", (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("username").GetString());
        await host.StopAsync();
    }

    [Fact]
    public async Task Login_PasswordMode_StillSignsIn()
    {
        using var host = await StartAsync(users: null);
        var res = await LoginAsync(host, AdminUser, AdminPass);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/qcli/auth/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await host.GetTestClient().SendAsync(req)).StatusCode);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("https://app.example.com/sign-in")]
    [InlineData("http://localhost:5000/login?next=%2Fadmin")]
    [InlineData("/sign-in")]
    [InlineData("/")]
    public void SignInUrl_Valid(string url) => Assert.True(AdminConfig.IsValidSignInUrl(url));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("sign-in")]
    [InlineData("//evil.example.com/sign-in")]
    [InlineData("/\\evil.example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/sign-in")]
    [InlineData("https://")]
    [InlineData("https://app.example.com/sign in")]
    [InlineData("/sign-in\n")]
    public void SignInUrl_Invalid(string url) => Assert.False(AdminConfig.IsValidSignInUrl(url));

    [Fact]
    public async Task SignInUrl_Invalid_FailsAtStartup()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(users: null, signInUrl: "javascript:alert(1)"));
        var argument = ex as ArgumentException ?? ex.InnerException as ArgumentException;
        Assert.NotNull(argument);
        Assert.Contains("SignInUrl", argument!.Message);
    }

    [Fact]
    public void SignInUrl_Null_IsAllowed()
    {
        var config = new AdminConfig(NullLogger<AdminConfig>.Instance) { SignInUrl = null };
        config.Validate();
    }
}
