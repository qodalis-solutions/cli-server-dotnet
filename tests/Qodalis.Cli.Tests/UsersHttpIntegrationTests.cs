using System.Net;
using System.Net.Http.Headers;
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

namespace Qodalis.Cli.Tests;

/// <summary>
/// HTTP integration tests for the Users plugin endpoints.
/// Builds a minimal ASP.NET Core test host with only the Users plugin registered,
/// exercising the full HTTP pipeline including the UsersAuthMiddleware.
/// </summary>
public class UsersHttpIntegrationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private IHost _host = null!;
    private HttpClient _client = null!;
    private string _adminToken = string.Empty;
    private string _adminUserId = string.Empty;

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
                            cli.AddUsers();
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

        // Wait a moment for the hosted bootstrap service to seed the admin user
        await Task.Delay(500);

        // Login as root to get admin token
        var loginRes = await LoginAsync("root", "root");
        _adminToken = loginRes.Token;
        _adminUserId = loginRes.Session.UserId;
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

    private HttpRequestMessage AuthRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        return req;
    }

    private async Task<LoginResponseDto> LoginAsync(string username, string password)
    {
        var res = await _client.PostAsJsonAsync("/api/v1/qcli/auth/login",
            new { username, password }, JsonOptions);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<LoginResponseDto>(JsonOptions))!;
    }

    private async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(content, JsonOptions);
    }

    // Minimal DTOs for deserialization
    private record LoginResponseDto(string Token, SessionDto Session);
    private record SessionDto(string Id, string UserId);

    // =========================================================================
    // Auth Tests
    // =========================================================================

    [Fact]
    public async Task Login_ValidCredentials_Returns200WithTokenAndSession()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/qcli/auth/login",
            new { username = "root", password = "root" }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await ReadJsonAsync(res);
        Assert.True(body.TryGetProperty("token", out var token));
        Assert.False(string.IsNullOrEmpty(token.GetString()));
        Assert.True(body.TryGetProperty("session", out _));
    }

    [Fact]
    public async Task Login_InvalidPassword_Returns401()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/qcli/auth/login",
            new { username = "root", password = "wrong" }, JsonOptions);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.True(body.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Login_UnknownUser_Returns401()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/qcli/auth/login",
            new { username = "nobody", password = "pass" }, JsonOptions);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Login_MissingUsername_Returns400()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/qcli/auth/login",
            new { username = "", password = "root" }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Login_MissingPassword_Returns400()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/qcli/auth/login",
            new { username = "root", password = "" }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Login_DisabledUser_Returns401()
    {
        var createRes = await CreateUserAsync("disabled-user", "disabled@test.com", "pass123");
        var userId = createRes.GetProperty("id").GetString()!;

        await UpdateUserAsync(userId, new { disabled = true });

        var loginRes = await _client.PostAsJsonAsync("/api/v1/qcli/auth/login",
            new { username = "disabled-user", password = "pass123" }, JsonOptions);

        Assert.Equal(HttpStatusCode.Unauthorized, loginRes.StatusCode);
    }

    [Fact]
    public async Task Logout_WithoutToken_Returns401()
    {
        var res = await _client.PostAsync("/api/v1/qcli/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Logout_WithInvalidToken_Returns401()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/qcli/auth/logout");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Logout_WithValidToken_Returns204()
    {
        var login = await LoginAsync("root", "root");

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/qcli/auth/logout");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        // Token should be invalidated — subsequent requests should fail
        var checkReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/qcli/users");
        checkReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        var checkRes = await _client.SendAsync(checkReq);
        Assert.Equal(HttpStatusCode.Unauthorized, checkRes.StatusCode);
    }

    [Fact]
    public async Task Verify_CorrectPassword_ReturnsValid()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/qcli/auth/verify",
            new { userId = _adminUserId, password = "root" }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.True(body.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Verify_WrongPassword_ReturnsNotValid()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/qcli/auth/verify",
            new { userId = _adminUserId, password = "wrong" }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.False(body.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Verify_MissingUserId_Returns400()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/qcli/auth/verify",
            new { userId = "", password = "root" }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Verify_MissingPassword_Returns400()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/qcli/auth/verify",
            new { userId = _adminUserId, password = "" }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // =========================================================================
    // Users CRUD Tests
    // =========================================================================

    [Fact]
    public async Task GetUsers_WithoutAuth_Returns401()
    {
        var res = await _client.GetAsync("/api/v1/qcli/users");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task GetUsers_WithAuth_Returns200WithList()
    {
        var req = AuthRequest(HttpMethod.Get, "/api/v1/qcli/users");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.True(body.GetArrayLength() > 0);
    }

    [Fact]
    public async Task CreateUser_Admin_Returns201()
    {
        var user = await CreateUserAsync("create-test-user", "create-test@test.com", "testpass");

        Assert.Equal("create-test-user", user.GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(user.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task CreateUser_DuplicateName_Returns409()
    {
        await CreateUserAsync("dup-user", "dup@test.com");

        var req = AuthRequest(HttpMethod.Post, "/api/v1/qcli/users");
        req.Content = JsonContent.Create(
            new { name = "dup-user", email = "dup2@test.com" }, options: JsonOptions);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task CreateUser_MissingName_Returns400()
    {
        var req = AuthRequest(HttpMethod.Post, "/api/v1/qcli/users");
        req.Content = JsonContent.Create(
            new { name = "", email = "noname@test.com" }, options: JsonOptions);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task CreateUser_MissingEmail_Returns400()
    {
        var req = AuthRequest(HttpMethod.Post, "/api/v1/qcli/users");
        req.Content = JsonContent.Create(
            new { name = "noemail-user", email = "" }, options: JsonOptions);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task GetUser_ByName_Returns200()
    {
        var req = AuthRequest(HttpMethod.Get, "/api/v1/qcli/users/root");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Equal("root", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetUser_Unknown_Returns404()
    {
        var req = AuthRequest(HttpMethod.Get, "/api/v1/qcli/users/no-such-user");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task UpdateUser_Admin_Returns200()
    {
        var created = await CreateUserAsync("update-test-user", "upd@test.com");
        var userId = created.GetProperty("id").GetString()!;

        var updated = await UpdateUserAsync(userId, new { email = "updated@test.com" });

        Assert.Equal("updated@test.com", updated.GetProperty("email").GetString());
    }

    [Fact]
    public async Task DeleteUser_Admin_Returns204AndCascades()
    {
        // Create user with password and login session
        var created = await CreateUserAsync("delete-cascade-user", "dc@test.com", "dcpass");
        var userId = created.GetProperty("id").GetString()!;

        // Login as the user to create a session
        var login = await LoginAsync("delete-cascade-user", "dcpass");

        // Delete the user
        var delReq = AuthRequest(HttpMethod.Delete, $"/api/v1/qcli/users/{userId}");
        var delRes = await _client.SendAsync(delReq);
        Assert.Equal(HttpStatusCode.NoContent, delRes.StatusCode);

        // User should no longer exist
        var getReq = AuthRequest(HttpMethod.Get, $"/api/v1/qcli/users/{userId}");
        var getRes = await _client.SendAsync(getReq);
        Assert.Equal(HttpStatusCode.NotFound, getRes.StatusCode);

        // Session token should no longer work
        var sessReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/qcli/users");
        sessReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        var sessRes = await _client.SendAsync(sessReq);
        Assert.Equal(HttpStatusCode.Unauthorized, sessRes.StatusCode);
    }

    // =========================================================================
    // Password Change Tests
    // =========================================================================

    [Fact]
    public async Task ChangePassword_AdminCanChangeOtherUser()
    {
        var created = await CreateUserAsync("pw-admin-change", "pwa@test.com", "oldpass");
        var userId = created.GetProperty("id").GetString()!;

        var req = AuthRequest(HttpMethod.Put, $"/api/v1/qcli/users/{userId}/password");
        req.Content = JsonContent.Create(
            new { newPassword = "newpass" }, options: JsonOptions);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // Verify new password works
        var loginRes = await _client.PostAsJsonAsync("/api/v1/qcli/auth/login",
            new { username = "pw-admin-change", password = "newpass" }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_SelfChangeWithCurrentPassword()
    {
        await CreateUserAsync("pw-self-change", "pws@test.com", "selfpass");
        var login = await LoginAsync("pw-self-change", "selfpass");

        var req = new HttpRequestMessage(HttpMethod.Put,
            $"/api/v1/qcli/users/{login.Session.UserId}/password");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        req.Content = JsonContent.Create(
            new { currentPassword = "selfpass", newPassword = "selfpass2" }, options: JsonOptions);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_SelfChangeWrongCurrentPassword_Returns401()
    {
        await CreateUserAsync("pw-wrong-curr", "pwwc@test.com", "rightpass");
        var login = await LoginAsync("pw-wrong-curr", "rightpass");

        var req = new HttpRequestMessage(HttpMethod.Put,
            $"/api/v1/qcli/users/{login.Session.UserId}/password");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        req.Content = JsonContent.Create(
            new { currentPassword = "wrongpass", newPassword = "newpass" }, options: JsonOptions);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_MissingNewPassword_Returns400()
    {
        var req = AuthRequest(HttpMethod.Put, $"/api/v1/qcli/users/{_adminUserId}/password");
        req.Content = JsonContent.Create(
            new { newPassword = "" }, options: JsonOptions);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_SelfChangeMissingCurrentPassword_Returns400()
    {
        await CreateUserAsync("pw-no-curr", "pwnc@test.com", "thepass");
        var login = await LoginAsync("pw-no-curr", "thepass");

        var req = new HttpRequestMessage(HttpMethod.Put,
            $"/api/v1/qcli/users/{login.Session.UserId}/password");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        req.Content = JsonContent.Create(
            new { newPassword = "newpass" }, options: JsonOptions);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // =========================================================================
    // Sessions Tests
    // =========================================================================

    [Fact]
    public async Task GetSessions_Admin_Returns200()
    {
        var req = AuthRequest(HttpMethod.Get, "/api/v1/qcli/sessions");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
    }

    [Fact]
    public async Task GetCurrentSession_Returns200()
    {
        var req = AuthRequest(HttpMethod.Get, "/api/v1/qcli/sessions/current");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task GetCurrentSession_WithoutAuth_Returns401()
    {
        var res = await _client.GetAsync("/api/v1/qcli/sessions/current");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task KickSession_Admin_Returns204AndInvalidatesToken()
    {
        // Create user and login
        await CreateUserAsync("kick-target", "kick@test.com", "kickpass");
        var login = await LoginAsync("kick-target", "kickpass");
        var sessionId = login.Session.Id;
        var targetToken = login.Token;

        // Admin kicks the session
        var kickReq = AuthRequest(HttpMethod.Delete, $"/api/v1/qcli/sessions/{sessionId}");
        var kickRes = await _client.SendAsync(kickReq);
        Assert.Equal(HttpStatusCode.NoContent, kickRes.StatusCode);

        // Token should no longer work
        var check = new HttpRequestMessage(HttpMethod.Get, "/api/v1/qcli/users");
        check.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetToken);
        var checkRes = await _client.SendAsync(check);
        Assert.Equal(HttpStatusCode.Unauthorized, checkRes.StatusCode);
    }

    [Fact]
    public async Task KickSession_NonexistentId_Returns404()
    {
        var req = AuthRequest(HttpMethod.Delete, "/api/v1/qcli/sessions/no-such-session-id");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // =========================================================================
    // Groups Tests
    // =========================================================================

    [Fact]
    public async Task CreateGroup_Admin_Returns201()
    {
        var req = AuthRequest(HttpMethod.Post, "/api/v1/qcli/groups");
        req.Content = JsonContent.Create(
            new { name = "editors", description = "Content editors" }, options: JsonOptions);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Equal("editors", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task CreateGroup_DuplicateName_Returns409()
    {
        var req1 = AuthRequest(HttpMethod.Post, "/api/v1/qcli/groups");
        req1.Content = JsonContent.Create(
            new { name = "dup-group" }, options: JsonOptions);
        await _client.SendAsync(req1);

        var req2 = AuthRequest(HttpMethod.Post, "/api/v1/qcli/groups");
        req2.Content = JsonContent.Create(
            new { name = "dup-group" }, options: JsonOptions);
        var res = await _client.SendAsync(req2);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task CreateGroup_MissingName_Returns400()
    {
        var req = AuthRequest(HttpMethod.Post, "/api/v1/qcli/groups");
        req.Content = JsonContent.Create(
            new { name = "" }, options: JsonOptions);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task GetGroups_Returns200()
    {
        var req = AuthRequest(HttpMethod.Get, "/api/v1/qcli/groups");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
    }

    [Fact]
    public async Task GetGroup_ByName_Returns200()
    {
        var req = AuthRequest(HttpMethod.Get, "/api/v1/qcli/groups/admin");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Equal("admin", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetGroup_Unknown_Returns404()
    {
        var req = AuthRequest(HttpMethod.Get, "/api/v1/qcli/groups/no-such-group");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task DeleteGroup_Admin_Returns204()
    {
        var createReq = AuthRequest(HttpMethod.Post, "/api/v1/qcli/groups");
        createReq.Content = JsonContent.Create(
            new { name = "temp-del-group" }, options: JsonOptions);
        await _client.SendAsync(createReq);

        var delReq = AuthRequest(HttpMethod.Delete, "/api/v1/qcli/groups/temp-del-group");
        var res = await _client.SendAsync(delReq);

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    [Fact]
    public async Task AddMember_Admin_Returns200()
    {
        var grpReq = AuthRequest(HttpMethod.Post, "/api/v1/qcli/groups");
        grpReq.Content = JsonContent.Create(
            new { name = "member-test-group" }, options: JsonOptions);
        await _client.SendAsync(grpReq);

        var user = await CreateUserAsync("member-test-user", "member@test.com");
        var userId = user.GetProperty("id").GetString()!;

        var req = AuthRequest(HttpMethod.Put, $"/api/v1/qcli/groups/member-test-group/members/{userId}");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task AddMember_GroupNotFound_Returns404()
    {
        var req = AuthRequest(HttpMethod.Put, "/api/v1/qcli/groups/nonexistent-group/members/some-id");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task AddMember_UserNotFound_Returns404()
    {
        var grpReq = AuthRequest(HttpMethod.Post, "/api/v1/qcli/groups");
        grpReq.Content = JsonContent.Create(
            new { name = "member-nouser-group" }, options: JsonOptions);
        await _client.SendAsync(grpReq);

        var req = AuthRequest(HttpMethod.Put, "/api/v1/qcli/groups/member-nouser-group/members/no-such-user-id");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task RemoveMember_Admin_Returns200()
    {
        var grpReq = AuthRequest(HttpMethod.Post, "/api/v1/qcli/groups");
        grpReq.Content = JsonContent.Create(
            new { name = "rm-member-group" }, options: JsonOptions);
        await _client.SendAsync(grpReq);

        var user = await CreateUserAsync("rm-member-user", "rmm@test.com");
        var userId = user.GetProperty("id").GetString()!;

        var addReq = AuthRequest(HttpMethod.Put, $"/api/v1/qcli/groups/rm-member-group/members/{userId}");
        await _client.SendAsync(addReq);

        var rmReq = AuthRequest(HttpMethod.Delete, $"/api/v1/qcli/groups/rm-member-group/members/{userId}");
        var res = await _client.SendAsync(rmReq);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task RemoveMember_UserNotFound_Returns404()
    {
        var req = AuthRequest(HttpMethod.Delete, "/api/v1/qcli/groups/admin/members/no-such-user-id");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // =========================================================================
    // Audit Tests
    // =========================================================================

    [Fact]
    public async Task GetAudit_Admin_Returns200()
    {
        var req = AuthRequest(HttpMethod.Get, "/api/v1/qcli/audit");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.True(body.GetArrayLength() > 0);
    }

    [Fact]
    public async Task GetAudit_FilterByEvent_Returns200()
    {
        var req = AuthRequest(HttpMethod.Get, "/api/v1/qcli/audit?event=login");
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        foreach (var entry in body.EnumerateArray())
        {
            Assert.Equal("login", entry.GetProperty("event").GetString());
        }
    }

    [Fact]
    public async Task GetAudit_WithoutAuth_Returns401()
    {
        var res = await _client.GetAsync("/api/v1/qcli/audit");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ClearAudit_Admin_Returns204()
    {
        var delReq = AuthRequest(HttpMethod.Delete, "/api/v1/qcli/audit");
        var delRes = await _client.SendAsync(delReq);
        Assert.Equal(HttpStatusCode.NoContent, delRes.StatusCode);

        var getReq = AuthRequest(HttpMethod.Get, "/api/v1/qcli/audit");
        var getRes = await _client.SendAsync(getReq);
        var body = await ReadJsonAsync(getRes);
        Assert.Equal(0, body.GetArrayLength());
    }

    // =========================================================================
    // Non-admin access tests
    // =========================================================================

    [Fact]
    public async Task NonAdmin_CannotCreateUsers_Returns403()
    {
        var bobToken = await CreateNonAdminAndLogin("nonadmin-create", "nac@test.com", "bobpass");

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/qcli/users");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobToken);
        req.Content = JsonContent.Create(
            new { name = "charlie", email = "charlie@test.com" }, options: JsonOptions);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_CannotUpdateUsers_Returns403()
    {
        var bobToken = await CreateNonAdminAndLogin("nonadmin-upd", "nau@test.com", "bobpass");

        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/qcli/users/{_adminUserId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobToken);
        req.Content = JsonContent.Create(
            new { email = "hacked@test.com" }, options: JsonOptions);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_CannotDeleteUsers_Returns403()
    {
        var bobToken = await CreateNonAdminAndLogin("nonadmin-del", "nad@test.com", "bobpass");

        var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/qcli/users/{_adminUserId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobToken);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_CannotListSessions_Returns403()
    {
        var bobToken = await CreateNonAdminAndLogin("nonadmin-sess", "nas@test.com", "bobpass");

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/qcli/sessions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobToken);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_CanGetOwnSession_Returns200()
    {
        var bobToken = await CreateNonAdminAndLogin("nonadmin-own-sess", "nos@test.com", "bobpass");

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/qcli/sessions/current");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobToken);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_CannotKickSessions_Returns403()
    {
        var bobToken = await CreateNonAdminAndLogin("nonadmin-kick", "nak@test.com", "bobpass");

        var req = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/qcli/sessions/some-id");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobToken);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_CannotCreateGroups_Returns403()
    {
        var bobToken = await CreateNonAdminAndLogin("nonadmin-crgrp", "nacg@test.com", "bobpass");

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/qcli/groups");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobToken);
        req.Content = JsonContent.Create(
            new { name = "hacker-group" }, options: JsonOptions);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_CannotDeleteGroups_Returns403()
    {
        var bobToken = await CreateNonAdminAndLogin("nonadmin-grp", "nag@test.com", "bobpass");

        var req = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/qcli/groups/admin");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobToken);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_CannotAccessAudit_Returns403()
    {
        var bobToken = await CreateNonAdminAndLogin("nonadmin-audit", "naa@test.com", "bobpass");

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/qcli/audit");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobToken);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_CannotClearAudit_Returns403()
    {
        var bobToken = await CreateNonAdminAndLogin("nonadmin-clraud", "naca@test.com", "bobpass");

        var req = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/qcli/audit");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobToken);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_CannotChangeOtherUserPassword_Returns403()
    {
        var bobToken = await CreateNonAdminAndLogin("nonadmin-pwother", "napo@test.com", "bobpass");

        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/qcli/users/{_adminUserId}/password");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobToken);
        req.Content = JsonContent.Create(
            new { newPassword = "hackedpass" }, options: JsonOptions);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // =========================================================================
    // Helper methods
    // =========================================================================

    private async Task<JsonElement> CreateUserAsync(string name, string email, string? password = null)
    {
        var req = AuthRequest(HttpMethod.Post, "/api/v1/qcli/users");
        var payload = new Dictionary<string, object?> { ["name"] = name, ["email"] = email };
        if (password != null) payload["password"] = password;
        req.Content = JsonContent.Create(payload, options: JsonOptions);

        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await ReadJsonAsync(res);
    }

    private async Task<JsonElement> UpdateUserAsync(string id, object dto)
    {
        var req = AuthRequest(HttpMethod.Put, $"/api/v1/qcli/users/{id}");
        req.Content = JsonContent.Create(dto, options: JsonOptions);
        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await ReadJsonAsync(res);
    }

    private async Task<string> CreateNonAdminAndLogin(string name, string email, string password)
    {
        await CreateUserAsync(name, email, password);
        var login = await LoginAsync(name, password);
        return login.Token;
    }
}
