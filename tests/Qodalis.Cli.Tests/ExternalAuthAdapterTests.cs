using System.Text.Json;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Tests.Helpers;
using static Qodalis.Cli.Tests.Helpers.UsersTestHost;

namespace Qodalis.Cli.Tests;

/// <summary>
/// Integration tests for the external auth adapter mode. A custom header (X-Test-User)
/// simulates host-app authentication; X-Test-Admin and X-Test-Groups shape the user.
/// </summary>
public class ExternalAuthAdapterTests : IAsyncLifetime
{
    private const string ExternalMessage = "Authentication is managed by the host application";
    private UsersTestHost _h = null!;

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync(options =>
        {
            options.UseExternalAuth(context =>
            {
                var userHeader = context.Request.Headers["X-Test-User"].FirstOrDefault();
                if (string.IsNullOrEmpty(userHeader))
                    return null;

                var isAdmin = context.Request.Headers["X-Test-Admin"].FirstOrDefault() == "true";
                var groups = context.Request.Headers["X-Test-Groups"].FirstOrDefault()?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [];

                return new ExternalAuthResult
                {
                    User = new User
                    {
                        Id = $"ext-{userHeader}",
                        Name = userHeader,
                        Email = $"{userHeader}@external.com",
                        Groups = groups,
                    },
                    IsAdmin = isAdmin,
                };
            });
        });
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? user = null,
        bool admin = false, string? groups = null, object? body = null)
    {
        var req = new HttpRequestMessage(method, $"{Base}/{path}");
        if (user != null)
        {
            req.Headers.Add("X-Test-User", user);
            if (admin) req.Headers.Add("X-Test-Admin", "true");
            if (groups != null) req.Headers.Add("X-Test-Groups", groups);
        }
        if (body != null) req.Content = System.Net.Http.Json.JsonContent.Create(body);
        return _h.Client.SendAsync(req);
    }

    // =========================================================================
    // Credential endpoints answer 501
    // =========================================================================

    [Fact]
    public async Task Login_Returns501()
    {
        var res = await SendAsync(HttpMethod.Post, "sessions", body: new { username = "root", password = "root" });
        Assert.Equal(ExternalMessage, await AssertErrorAsync(res, 501));
    }

    [Fact]
    public async Task Logout_Returns501()
    {
        var res = await SendAsync(HttpMethod.Delete, "sessions/current", "admin-user", admin: true);
        Assert.Equal(ExternalMessage, await AssertErrorAsync(res, 501));
    }

    [Fact]
    public async Task VerifyPassword_Returns501()
    {
        var res = await SendAsync(HttpMethod.Post, "users/some-id/verify-password", "admin-user", admin: true,
            body: new { password = "some-pass" });
        Assert.Equal(ExternalMessage, await AssertErrorAsync(res, 501));
    }

    [Fact]
    public async Task ChangePassword_Returns501()
    {
        var res = await SendAsync(HttpMethod.Put, "users/some-id/password", "admin-user", admin: true,
            body: new { password = "some-pass" });
        Assert.Equal(ExternalMessage, await AssertErrorAsync(res, 501));
    }

    // =========================================================================
    // Seeding
    // =========================================================================

    [Fact]
    public async Task BuiltInGroups_AreSeeded_RootIsNot()
    {
        var groups = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "groups", "admin-user", admin: true));
        var names = groups.EnumerateArray().Select(g => g.GetProperty("name").GetString()).ToList();
        Assert.Contains("admin", names);
        Assert.Contains("users", names);

        var users = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "users", "admin-user", admin: true));
        Assert.Equal(0, users.GetArrayLength());
    }

    // =========================================================================
    // Adapter authenticates requests; permissions come from groups
    // =========================================================================

    [Fact]
    public async Task GetUsers_WithoutAuthHeader_Returns401()
    {
        await AssertErrorAsync(await SendAsync(HttpMethod.Get, "users"), 401);
    }

    [Fact]
    public async Task AdminAdapterUser_HasAllPermissions()
    {
        Assert.Equal(200, (int)(await SendAsync(HttpMethod.Get, "users", "alice", admin: true)).StatusCode);

        var created = await SendAsync(HttpMethod.Post, "users", "alice", admin: true,
            body: new { name = "ext-new-user", email = "ext@test.com" });
        Assert.Equal(201, (int)created.StatusCode);
        Assert.Equal("ext-new-user", (await ReadJsonAsync(created)).GetProperty("name").GetString());

        var current = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "sessions/current", "alice", admin: true));
        Assert.Equal("alice", current.GetProperty("user").GetProperty("name").GetString());
        Assert.Contains("admin", current.GetProperty("user").GetProperty("groups").EnumerateArray().Select(g => g.GetString()));
        Assert.Equal("*", current.GetProperty("permissions")[0].GetString());
    }

    [Fact]
    public async Task NonAdminAdapterUser_IsDeniedAdminEndpoints()
    {
        await AssertErrorAsync(await SendAsync(HttpMethod.Get, "users", "regular-user"), 403);
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "users", "regular-user",
            body: new { name = "should-fail", email = "fail@test.com" }), 403);
        await AssertErrorAsync(await SendAsync(HttpMethod.Get, "audit", "regular-user"), 403);
        Assert.Equal(200, (int)(await SendAsync(HttpMethod.Get, "groups", "regular-user")).StatusCode);

        var current = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "sessions/current", "bob"));
        Assert.Equal("bob", current.GetProperty("user").GetProperty("name").GetString());
        Assert.Equal(0, current.GetProperty("permissions").GetArrayLength());
    }

    [Fact]
    public async Task AdapterUser_InCustomGroup_GetsGroupPermissions()
    {
        var create = await SendAsync(HttpMethod.Post, "groups", "alice", admin: true,
            body: new { name = "auditors", permissions = new[] { "audit:read" } });
        Assert.Equal(201, (int)create.StatusCode);

        Assert.Equal(200, (int)(await SendAsync(HttpMethod.Get, "audit", "carol", groups: "AUDITORS")).StatusCode);
        await AssertErrorAsync(await SendAsync(HttpMethod.Delete, "audit", "carol", groups: "auditors"), 403);
    }

    [Fact]
    public async Task AdapterSessions_AreNotListed()
    {
        await SendAsync(HttpMethod.Get, "sessions/current", "bob");
        var res = await SendAsync(HttpMethod.Get, "sessions", "admin-user", admin: true);
        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal(0, (await ReadJsonAsync(res)).GetArrayLength());
    }

    [Fact]
    public async Task NonProtectedRoute_PassesThrough()
    {
        var res = await _h.Client.GetAsync("/api/v1/qcli/version");
        Assert.NotEqual(401, (int)res.StatusCode);
    }
}
