using System.Text.Json;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Tests.Helpers;
using static Qodalis.Cli.Tests.Helpers.UsersTestHost;

namespace Qodalis.Cli.Tests;

/// <summary>
/// HTTP integration tests for the users plugin, covering the server contract
/// (web-cli spec "Appendix A — Server contract").
/// </summary>
public class UsersHttpIntegrationTests : IAsyncLifetime
{
    private UsersTestHost _h = null!;
    private string _root = string.Empty;
    private string _rootId = string.Empty;

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync();
        var (token, session) = await _h.LoginAsync("root", "root");
        _root = token;
        _rootId = session.GetProperty("user").GetProperty("id").GetString()!;
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private static string[] Names(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToArray();

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    private async Task<List<JsonElement>> AuditAsync(string? query = null)
    {
        var res = await _h.GetAsync("audit" + (query != null ? "?" + query : ""), _root);
        Assert.Equal(200, (int)res.StatusCode);
        return (await ReadJsonAsync(res)).EnumerateArray().ToList();
    }

    // =========================================================================
    // Login / logout
    // =========================================================================

    [Fact]
    public async Task Login_Returns201WithTokenAndSessionShape()
    {
        var res = await _h.PostAsync("sessions", null, new { username = "root", password = "root" });
        Assert.Equal(201, (int)res.StatusCode);

        var body = await ReadJsonAsync(res);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));

        var session = body.GetProperty("session");
        Assert.False(string.IsNullOrEmpty(session.GetProperty("id").GetString()));
        var user = session.GetProperty("user");
        Assert.Equal("root", user.GetProperty("name").GetString());
        Assert.Equal("root@localhost", user.GetProperty("email").GetString());
        Assert.Equal(["admin"], Strings(user.GetProperty("groups")));
        Assert.False(user.GetProperty("disabled").GetBoolean());
        Assert.Equal(["*"], Strings(session.GetProperty("permissions")));

        var lastActivity = session.GetProperty("lastActivity").GetInt64();
        Assert.True(session.GetProperty("loginTime").GetInt64() > 0);
        Assert.Equal(lastActivity + 30 * 60 * 1000, session.GetProperty("expiresAt").GetInt64());
        Assert.False(session.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task Login_ByEmail_Returns201()
    {
        var (_, session) = await _h.LoginAsync("ROOT@localhost", "root");
        Assert.Equal("root", session.GetProperty("user").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401Error()
    {
        var res = await _h.PostAsync("sessions", null, new { username = "root", password = "wrong" });
        Assert.Equal("invalid credentials", await AssertErrorAsync(res, 401));
    }

    [Fact]
    public async Task Login_UnknownUser_Returns401()
    {
        var res = await _h.PostAsync("sessions", null, new { username = "nobody", password = "pass" });
        await AssertErrorAsync(res, 401);
    }

    [Theory]
    [InlineData("{\"username\":\"\",\"password\":\"root\"}")]
    [InlineData("{\"username\":\"root\",\"password\":\"\"}")]
    [InlineData("{\"username\":\"root\"}")]
    [InlineData("{}")]
    [InlineData("{not json")]
    [InlineData("{\"username\":123,\"password\":true}")]
    public async Task Login_BadBody_Returns400Error(string body)
    {
        var res = await _h.SendAsync(HttpMethod.Post, "sessions", null, body);
        await AssertErrorAsync(res, 400);
    }

    [Fact]
    public async Task Login_EmptyBody_Returns400Error()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/sessions")
        {
            Content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json"),
        };
        await AssertErrorAsync(await _h.Client.SendAsync(req), 400);
    }

    [Fact]
    public async Task Login_DisabledUser_Returns403AccountDisabled()
    {
        await _h.CreateUserAsync(_root, "dis-user");
        Assert.Equal(200, (int)(await _h.PutAsync("users/dis-user", _root, new { disabled = true })).StatusCode);

        var res = await _h.PostAsync("sessions", null, new { username = "dis-user", password = "pass1234" });
        Assert.Equal("account disabled", await AssertErrorAsync(res, 403));
    }

    [Fact]
    public async Task Login_FiveFailures_LocksAccountFor5Minutes()
    {
        await _h.CreateUserAsync(_root, "locky");
        for (var i = 0; i < 5; i++)
            await AssertErrorAsync(await _h.PostAsync("sessions", null, new { username = "locky", password = "bad" }), 401);

        // Locked: even the correct password is refused, by name or email.
        var locked = await _h.PostAsync("sessions", null, new { username = "locky", password = "pass1234" });
        Assert.Equal("account locked, try again later", await AssertErrorAsync(locked, 429));
        await AssertErrorAsync(await _h.PostAsync("sessions", null, new { username = "locky@test.com", password = "pass1234" }), 429);

        _h.Time.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));
        await _h.LoginAsync("locky", "pass1234");
    }

    [Fact]
    public async Task Login_SuccessResetsFailureCount()
    {
        await _h.CreateUserAsync(_root, "resetty");
        for (var i = 0; i < 4; i++)
            await _h.PostAsync("sessions", null, new { username = "resetty", password = "bad" });
        await _h.LoginAsync("resetty", "pass1234");
        for (var i = 0; i < 4; i++)
            await AssertErrorAsync(await _h.PostAsync("sessions", null, new { username = "resetty", password = "bad" }), 401);
        await _h.LoginAsync("resetty", "pass1234");
    }

    [Fact]
    public async Task Login_PersistsLastLoginAt()
    {
        await _h.CreateUserAsync(_root, "lla");
        var before = await ReadJsonAsync(await _h.GetAsync("users/lla", _root));
        Assert.False(before.TryGetProperty("lastLoginAt", out _));

        await _h.LoginAsync("lla", "pass1234");
        var after = await ReadJsonAsync(await _h.GetAsync("users/lla", _root));
        Assert.Equal(_h.Time.GetUtcNow().ToUnixTimeMilliseconds(), after.GetProperty("lastLoginAt").GetInt64());
    }

    [Fact]
    public async Task Logout_WithoutToken_Returns401()
    {
        await AssertErrorAsync(await _h.DeleteAsync("sessions/current"), 401);
    }

    [Fact]
    public async Task Logout_WithInvalidToken_Returns401()
    {
        await AssertErrorAsync(await _h.DeleteAsync("sessions/current", "invalid-token"), 401);
    }

    [Fact]
    public async Task Logout_WithValidToken_Returns204AndInvalidatesToken()
    {
        var (token, _) = await _h.LoginAsync("root", "root");
        Assert.Equal(204, (int)(await _h.DeleteAsync("sessions/current", token)).StatusCode);
        await AssertErrorAsync(await _h.GetAsync("sessions/current", token), 401);

        var logout = (await AuditAsync("event=logout")).Single();
        Assert.Equal("root", logout.GetProperty("username").GetString());
    }

    [Theory]
    [InlineData("users/")]
    [InlineData("groups/")]
    [InlineData("sessions/")]
    [InlineData("audit/")]
    [InlineData("sessions/current/")]
    public async Task Collections_AcceptTrailingSlash(string path)
    {
        var res = await _h.GetAsync(path, _root);
        Assert.Equal(200, (int)res.StatusCode);
    }

    [Fact]
    public async Task Login_AcceptsTrailingSlash()
    {
        var res = await _h.PostAsync("sessions/", null, new { username = "root", password = "root" });
        Assert.Equal(201, (int)res.StatusCode);
    }

    [Fact]
    public async Task UnrelatedPathSharingPrefix_IsNotProtected()
    {
        var res = await _h.GetAsync("/api/v1/qcli/users-x");
        Assert.NotEqual(401, (int)res.StatusCode);
    }

    // =========================================================================
    // Sessions
    // =========================================================================

    [Fact]
    public async Task GetCurrentSession_ReturnsLoginSessionShape()
    {
        var res = await _h.GetAsync("sessions/current", _root);
        Assert.Equal(200, (int)res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Equal("root", body.GetProperty("user").GetProperty("name").GetString());
        Assert.Equal(["*"], Strings(body.GetProperty("permissions")));
        Assert.True(body.TryGetProperty("loginTime", out _));
        Assert.Equal(body.GetProperty("lastActivity").GetInt64() + 30 * 60 * 1000, body.GetProperty("expiresAt").GetInt64());
        Assert.False(body.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task GetCurrentSession_WithoutAuth_Returns401()
    {
        await AssertErrorAsync(await _h.GetAsync("sessions/current"), 401);
    }

    [Fact]
    public async Task ListSessions_NeverIncludesTokens_AndMarksCurrent()
    {
        await _h.CreateUserAsync(_root, "lister");
        var (otherToken, _) = await _h.LoginAsync("lister", "pass1234");

        var res = await _h.GetAsync("sessions", _root);
        Assert.Equal(200, (int)res.StatusCode);
        var raw = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_root, raw);
        Assert.DoesNotContain(otherToken, raw);

        var sessions = JsonSerializer.Deserialize<JsonElement>(raw).EnumerateArray().ToList();
        Assert.Equal(2, sessions.Count);
        foreach (var s in sessions)
        {
            Assert.False(s.TryGetProperty("token", out _));
            foreach (var key in new[] { "id", "userId", "userName", "createdAt", "lastActivity", "expiresAt", "metadata" })
                Assert.True(s.TryGetProperty(key, out _), $"missing {key}");
        }
        var mine = sessions.Single(s => s.GetProperty("userName").GetString() == "root");
        Assert.True(mine.GetProperty("current").GetBoolean());
        Assert.Equal(_rootId, mine.GetProperty("userId").GetString());
        var theirs = sessions.Single(s => s.GetProperty("userName").GetString() == "lister");
        Assert.False(theirs.TryGetProperty("current", out var cur) && cur.GetBoolean());
    }

    [Fact]
    public async Task Session_IdleTimeout_ExpiresAndDeletes()
    {
        var (token, session) = await _h.LoginAsync("root", "root");
        var sessionId = session.GetProperty("id").GetString();

        _h.Time.Advance(TimeSpan.FromMinutes(31));
        await AssertErrorAsync(await _h.GetAsync("sessions/current", token), 401);

        var (fresh, _) = await _h.LoginAsync("root", "root");
        var listed = await ReadJsonAsync(await _h.GetAsync("sessions", fresh));
        Assert.DoesNotContain(listed.EnumerateArray(), s => s.GetProperty("id").GetString() == sessionId);
    }

    [Fact]
    public async Task Session_LastActivitySlides()
    {
        var (token, _) = await _h.LoginAsync("root", "root");
        for (var i = 0; i < 3; i++)
        {
            _h.Time.Advance(TimeSpan.FromMinutes(20));
            var res = await _h.GetAsync("sessions/current", token);
            Assert.Equal(200, (int)res.StatusCode);
            var body = await ReadJsonAsync(res);
            Assert.Equal(_h.Time.GetUtcNow().ToUnixTimeMilliseconds(), body.GetProperty("lastActivity").GetInt64());
        }
    }

    [Fact]
    public async Task RevokeSession_Admin_Returns204_InvalidatesToken_AndBroadcasts()
    {
        var created = await _h.CreateUserAsync(_root, "kick-target");
        var (token, session) = await _h.LoginAsync("kick-target", "pass1234");
        var sessionId = session.GetProperty("id").GetString();

        Assert.Equal(204, (int)(await _h.DeleteAsync($"sessions/{sessionId}", _root)).StatusCode);
        await AssertErrorAsync(await _h.GetAsync("sessions/current", token), 401);

        var kicked = Assert.Single(_h.Events.SessionKicked());
        Assert.Equal(sessionId, kicked.GetProperty("sessionId").GetString());
        Assert.Equal(created.GetProperty("id").GetString(), kicked.GetProperty("userId").GetString());
        Assert.Equal("revoked", kicked.GetProperty("reason").GetString());

        var audit = (await AuditAsync("event=session_revoked")).Single();
        Assert.Equal("kick-target", audit.GetProperty("username").GetString());
        Assert.Equal("root", audit.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task RevokeSession_OwnSessionWithoutPermission_Returns204()
    {
        await _h.CreateUserAsync(_root, "self-revoker");
        var (t1, _) = await _h.LoginAsync("self-revoker", "pass1234");
        var (_, s2) = await _h.LoginAsync("self-revoker", "pass1234");

        var res = await _h.DeleteAsync($"sessions/{s2.GetProperty("id").GetString()}", t1);
        Assert.Equal(204, (int)res.StatusCode);
        Assert.Equal(200, (int)(await _h.GetAsync("sessions/current", t1)).StatusCode);
    }

    [Fact]
    public async Task RevokeSession_OthersSessionWithoutPermission_Returns403()
    {
        await _h.CreateUserAsync(_root, "nosy");
        var (token, _) = await _h.LoginAsync("nosy", "pass1234");
        var rootSession = (await ReadJsonAsync(await _h.GetAsync("sessions/current", _root))).GetProperty("id").GetString();

        await AssertErrorAsync(await _h.DeleteAsync($"sessions/{rootSession}", token), 403);
        await AssertErrorAsync(await _h.DeleteAsync("sessions/no-such-session", token), 403);
    }

    [Fact]
    public async Task RevokeSession_UnknownId_Admin_Returns404()
    {
        await AssertErrorAsync(await _h.DeleteAsync("sessions/no-such-session-id", _root), 404);
    }

    [Fact]
    public async Task Session_LiveUserResolution_GroupChangesApplyImmediately()
    {
        await _h.CreateGroupAsync(_root, "readers", "users:read");
        await _h.CreateUserAsync(_root, "live-user");
        var (token, session) = await _h.LoginAsync("live-user", "pass1234");
        Assert.Equal(0, session.GetProperty("permissions").GetArrayLength());

        await AssertErrorAsync(await _h.GetAsync("users", token), 403);

        Assert.Equal(204, (int)(await _h.PostAsync("groups/readers/members/live-user", _root)).StatusCode);
        Assert.Equal(200, (int)(await _h.GetAsync("users", token)).StatusCode);
        var current = await ReadJsonAsync(await _h.GetAsync("sessions/current", token));
        Assert.Contains("users:read", Strings(current.GetProperty("permissions")));
        Assert.Contains("readers", Strings(current.GetProperty("user").GetProperty("groups")));

        // Group permission change also applies immediately.
        Assert.Equal(200, (int)(await _h.PutAsync("groups/readers", _root, new { permissions = Array.Empty<string>() })).StatusCode);
        await AssertErrorAsync(await _h.GetAsync("users", token), 403);
    }

    [Fact]
    public async Task Session_DisabledUser_Returns401_AndSessionsRevokedWithBroadcast()
    {
        var created = await _h.CreateUserAsync(_root, "to-disable");
        var (token, _) = await _h.LoginAsync("to-disable", "pass1234");

        Assert.Equal(200, (int)(await _h.PutAsync("users/to-disable", _root, new { disabled = true })).StatusCode);
        await AssertErrorAsync(await _h.GetAsync("sessions/current", token), 401);

        var kicked = Assert.Single(_h.Events.SessionKicked());
        Assert.Equal(created.GetProperty("id").GetString(), kicked.GetProperty("userId").GetString());
        Assert.Equal("user_disabled", kicked.GetProperty("reason").GetString());
    }

    // =========================================================================
    // Users CRUD
    // =========================================================================

    [Fact]
    public async Task GetUsers_WithoutAuth_Returns401()
    {
        await AssertErrorAsync(await _h.GetAsync("users"), 401);
    }

    [Fact]
    public async Task CreateUser_DefaultsAndOmitsAbsentOptionalFields()
    {
        var res = await _h.PostAsync("users", _root, new { name = "plain", email = "plain@test.com" });
        Assert.Equal(201, (int)res.StatusCode);
        var user = await ReadJsonAsync(res);

        Assert.Equal("plain", user.GetProperty("name").GetString());
        Assert.Equal(["users"], Strings(user.GetProperty("groups")));
        Assert.False(user.GetProperty("disabled").GetBoolean());
        Assert.True(user.GetProperty("createdAt").GetInt64() > 0);
        Assert.True(user.GetProperty("updatedAt").GetInt64() > 0);
        foreach (var absent in new[] { "homeDir", "lastLoginAt", "data", "password" })
            Assert.False(user.TryGetProperty(absent, out _), $"{absent} should be omitted");
    }

    [Fact]
    public async Task CreateUser_WithAllFields()
    {
        var res = await _h.PostAsync("users", _root, new
        {
            name = "full",
            email = "full@test.com",
            groups = new[] { "ADMIN" },
            password = "secret",
            disabled = true,
            homeDir = "/home/full",
            data = new { theme = "dark" },
        });
        Assert.Equal(201, (int)res.StatusCode);
        var user = await ReadJsonAsync(res);
        Assert.Equal(["admin"], Strings(user.GetProperty("groups")));
        Assert.True(user.GetProperty("disabled").GetBoolean());
        Assert.Equal("/home/full", user.GetProperty("homeDir").GetString());
        Assert.Equal("dark", user.GetProperty("data").GetProperty("theme").GetString());

        var audit = (await AuditAsync("event=user_created")).Single(e => e.GetProperty("username").GetString() == "full");
        Assert.Equal("root", audit.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task CreateUser_DuplicateNameOrEmail_Returns409()
    {
        await _h.CreateUserAsync(_root, "dup-user");
        await AssertErrorAsync(await _h.PostAsync("users", _root, new { name = "DUP-USER", email = "other@test.com" }), 409);
        await AssertErrorAsync(await _h.PostAsync("users", _root, new { name = "someone", email = "Dup-User@Test.com" }), 409);
    }

    [Theory]
    [InlineData("{\"name\":\"\",\"email\":\"a@b.c\"}")]
    [InlineData("{\"name\":\"x\",\"email\":\"\"}")]
    [InlineData("{\"email\":\"a@b.c\"}")]
    [InlineData("{\"name\":\"x\",\"email\":\"a@b.c\",\"password\":\"abc\"}")]
    [InlineData("{\"name\":\"x\",\"email\":\"a@b.c\",\"disabled\":\"yes\"}")]
    [InlineData("{\"name\":\"x\",\"email\":\"a@b.c\",\"groups\":\"admin\"}")]
    [InlineData("not json")]
    public async Task CreateUser_InvalidBody_Returns400Error(string body)
    {
        await AssertErrorAsync(await _h.SendAsync(HttpMethod.Post, "users", _root, body), 400);
    }

    [Fact]
    public async Task CreateUser_UnknownGroup_Returns400()
    {
        var res = await _h.PostAsync("users", _root, new { name = "g", email = "g@test.com", groups = new[] { "nope" } });
        Assert.Equal("unknown group: nope", await AssertErrorAsync(res, 400));
    }

    [Fact]
    public async Task CreateUser_PasswordPolicyIsConfigurable()
    {
        await using var host = await UsersTestHost.StartAsync(o => o.MinPasswordLength = 8);
        var token = await host.RootTokenAsync();
        await AssertErrorAsync(await host.PostAsync("users", token, new { name = "p", email = "p@t.c", password = "1234567" }), 400);
        Assert.Equal(201, (int)(await host.PostAsync("users", token, new { name = "p", email = "p@t.c", password = "12345678" })).StatusCode);
    }

    [Fact]
    public async Task GetUser_ByIdOrName()
    {
        Assert.Equal("root", (await ReadJsonAsync(await _h.GetAsync("users/root", _root))).GetProperty("name").GetString());
        Assert.Equal("root", (await ReadJsonAsync(await _h.GetAsync($"users/{_rootId}", _root))).GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetUser_Unknown_Returns404()
    {
        await AssertErrorAsync(await _h.GetAsync("users/no-such-user", _root), 404);
    }

    [Fact]
    public async Task ListUsers_QuerySkipTake()
    {
        await _h.CreateUserAsync(_root, "qa-1", email: "one@query.io");
        await _h.CreateUserAsync(_root, "qa-2", email: "two@query.io");
        await _h.CreateUserAsync(_root, "qa-3", email: "three@elsewhere.io");

        Assert.Equal(["qa-1", "qa-2", "qa-3"], Names(await ReadJsonAsync(await _h.GetAsync("users?query=QA-", _root))));
        Assert.Equal(["qa-1", "qa-2"], Names(await ReadJsonAsync(await _h.GetAsync("users?query=QUERY.io", _root))));
        Assert.Equal(["qa-2"], Names(await ReadJsonAsync(await _h.GetAsync("users?query=qa-&skip=1&take=1", _root))));
        Assert.Equal(4, (await ReadJsonAsync(await _h.GetAsync("users", _root))).GetArrayLength());
        Assert.Equal(["root"], Names(await ReadJsonAsync(await _h.GetAsync("users?take=1", _root))));

        await AssertErrorAsync(await _h.GetAsync("users?skip=-1", _root), 400);
        await AssertErrorAsync(await _h.GetAsync("users?take=abc", _root), 400);
    }

    [Fact]
    public async Task UpdateUser_Admin_Returns200()
    {
        await _h.CreateUserAsync(_root, "upd");
        var res = await _h.PutAsync("users/upd", _root, new { email = "updated@test.com", homeDir = "/h", data = new { a = 1 } });
        Assert.Equal(200, (int)res.StatusCode);
        var user = await ReadJsonAsync(res);
        Assert.Equal("updated@test.com", user.GetProperty("email").GetString());
        Assert.Equal("/h", user.GetProperty("homeDir").GetString());
        Assert.Equal(1, user.GetProperty("data").GetProperty("a").GetInt32());
    }

    [Fact]
    public async Task UpdateUser_Rename_AndDuplicateChecks()
    {
        await _h.CreateUserAsync(_root, "ren-a");
        await _h.CreateUserAsync(_root, "ren-b");

        await AssertErrorAsync(await _h.PutAsync("users/ren-a", _root, new { name = "REN-B" }), 409);
        await AssertErrorAsync(await _h.PutAsync("users/ren-a", _root, new { email = "ren-b@test.com" }), 409);

        var res = await _h.PutAsync("users/ren-a", _root, new { name = "ren-c" });
        Assert.Equal("ren-c", (await ReadJsonAsync(res)).GetProperty("name").GetString());
        Assert.Equal(200, (int)(await _h.GetAsync("users/ren-c", _root)).StatusCode);
    }

    [Fact]
    public async Task UpdateUser_UnknownGroup_Returns400_UnknownUser_Returns404()
    {
        await _h.CreateUserAsync(_root, "ug");
        Assert.Equal("unknown group: ghosts",
            await AssertErrorAsync(await _h.PutAsync("users/ug", _root, new { groups = new[] { "ghosts" } }), 400));
        await AssertErrorAsync(await _h.PutAsync("users/nobody", _root, new { email = "x@y.z" }), 404);
        await AssertErrorAsync(await _h.SendAsync(HttpMethod.Put, "users/ug", _root, "{\"disabled\":\"nope\"}"), 400);
    }

    [Fact]
    public async Task UpdateUser_EnableDisable_AreAudited()
    {
        await _h.CreateUserAsync(_root, "toggler");
        await _h.PutAsync("users/toggler", _root, new { disabled = true });
        await _h.PutAsync("users/toggler", _root, new { disabled = false });

        var events = (await AuditAsync("username=toggler")).Select(e => e.GetProperty("event").GetString()).ToList();
        Assert.Contains("user_disabled", events);
        Assert.Contains("user_enabled", events);
        Assert.Contains("user_updated", events);
    }

    [Fact]
    public async Task DeleteUser_CascadesCredentialsAndSessions_AndBroadcasts()
    {
        var created = await _h.CreateUserAsync(_root, "del-cascade");
        var (token, _) = await _h.LoginAsync("del-cascade", "pass1234");

        Assert.Equal(204, (int)(await _h.DeleteAsync("users/del-cascade", _root)).StatusCode);
        await AssertErrorAsync(await _h.GetAsync("users/del-cascade", _root), 404);
        await AssertErrorAsync(await _h.GetAsync("sessions/current", token), 401);

        var kicked = Assert.Single(_h.Events.SessionKicked());
        Assert.Equal("user_deleted", kicked.GetProperty("reason").GetString());

        // Credentials are gone: re-creating the user without a password cannot log in with the old one.
        await _h.CreateUserAsync(_root, "del-cascade", password: null);
        await AssertErrorAsync(await _h.PostAsync("sessions", null, new { username = "del-cascade", password = "pass1234" }), 401);

        var audit = (await AuditAsync("event=user_deleted")).Single();
        Assert.Equal("del-cascade", audit.GetProperty("username").GetString());
        Assert.Equal("root", audit.GetProperty("actor").GetString());
        Assert.Equal(created.GetProperty("name").GetString(), audit.GetProperty("username").GetString());
    }

    [Fact]
    public async Task DeleteUser_Unknown_Returns404()
    {
        await AssertErrorAsync(await _h.DeleteAsync("users/ghost", _root), 404);
    }

    // =========================================================================
    // Protections
    // =========================================================================

    [Fact]
    public async Task DeleteSelf_Returns409()
    {
        Assert.Equal("cannot delete yourself", await AssertErrorAsync(await _h.DeleteAsync("users/root", _root), 409));
    }

    [Fact]
    public async Task LastAdmin_CannotBeDeletedDisabledOrRemovedFromAdmin()
    {
        var ops = await _h.UserWithPermissionsAsync(_root, "ops", "users:*", "groups:*");

        await AssertErrorAsync(await _h.DeleteAsync("users/root", ops), 409);
        await AssertErrorAsync(await _h.PutAsync("users/root", ops, new { disabled = true }), 409);
        await AssertErrorAsync(await _h.PutAsync("users/root", ops, new { groups = new[] { "users" } }), 409);
        await AssertErrorAsync(await _h.DeleteAsync("groups/admin/members/root", ops), 409);
        await AssertErrorAsync(await _h.DeleteAsync("groups/ADMIN/members/ROOT", ops), 409);

        Assert.Equal(200, (int)(await _h.GetAsync("sessions/current", _root)).StatusCode);
    }

    [Fact]
    public async Task LastAdmin_DisabledAdminsDoNotCount()
    {
        await _h.CreateUserAsync(_root, "admin2", groups: ["admin"]);
        var ops = await _h.UserWithPermissionsAsync(_root, "ops2", "users:*");
        Assert.Equal(200, (int)(await _h.PutAsync("users/admin2", ops, new { disabled = true })).StatusCode);

        await AssertErrorAsync(await _h.PutAsync("users/root", ops, new { disabled = true }), 409);
    }

    [Fact]
    public async Task SecondAdmin_AllowsRemovingAdminFromRoot()
    {
        await _h.CreateUserAsync(_root, "admin2", groups: ["admin"]);
        var admin2 = (await _h.LoginAsync("admin2", "pass1234")).Token;

        Assert.Equal(204, (int)(await _h.DeleteAsync("groups/admin/members/root", admin2)).StatusCode);
        // root lost "*" immediately.
        await AssertErrorAsync(await _h.GetAsync("users", _root), 403);
        // admin2 is now the last admin.
        await AssertErrorAsync(await _h.PutAsync("users/admin2", admin2, new { groups = new[] { "users" } }), 409);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("users")]
    [InlineData("Admin")]
    public async Task BuiltInGroups_CannotBeDeleted(string name)
    {
        await AssertErrorAsync(await _h.DeleteAsync($"groups/{name}", _root), 409);
    }

    [Fact]
    public async Task AdminGroupPermissions_CannotBeChanged()
    {
        await AssertErrorAsync(await _h.PutAsync("groups/admin", _root, new { permissions = new[] { "users:read" } }), 409);

        var res = await _h.PutAsync("groups/admin", _root, new { description = "Super users", permissions = new[] { "*" } });
        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal("Super users", (await ReadJsonAsync(res)).GetProperty("description").GetString());
    }

    // =========================================================================
    // Permission matrix (non-admin users in custom groups)
    // =========================================================================

    [Fact]
    public async Task PlainUser_SelfServiceOnly()
    {
        var other = await _h.CreateUserAsync(_root, "other-user");
        var me = await _h.CreateUserAsync(_root, "plain-user");
        var token = (await _h.LoginAsync("plain-user", "pass1234")).Token;
        var otherId = other.GetProperty("id").GetString();

        await AssertErrorAsync(await _h.GetAsync("users", token), 403);
        Assert.Equal(200, (int)(await _h.GetAsync("users/plain-user", token)).StatusCode);
        Assert.Equal(200, (int)(await _h.GetAsync($"users/{me.GetProperty("id").GetString()}", token)).StatusCode);
        await AssertErrorAsync(await _h.GetAsync($"users/{otherId}", token), 403);
        await AssertErrorAsync(await _h.GetAsync("users/does-not-exist", token), 403);

        var perms = await ReadJsonAsync(await _h.GetAsync("users/plain-user/permissions", token));
        Assert.Equal(me.GetProperty("id").GetString(), perms.GetProperty("userId").GetString());
        Assert.Equal(["users"], Strings(perms.GetProperty("groups")));
        Assert.Equal(0, perms.GetProperty("permissions").GetArrayLength());
        await AssertErrorAsync(await _h.GetAsync("users/other-user/permissions", token), 403);

        await AssertErrorAsync(await _h.PostAsync("users", token, new { name = "x", email = "x@x.x" }), 403);
        await AssertErrorAsync(await _h.PutAsync("users/other-user", token, new { email = "h@h.h" }), 403);
        await AssertErrorAsync(await _h.DeleteAsync("users/other-user", token), 403);
        await AssertErrorAsync(await _h.DeleteAsync("users/plain-user", token), 403);
        await AssertErrorAsync(await _h.PutAsync("users/root/password", token, new { password = "hacked" }), 403);
        await AssertErrorAsync(await _h.PostAsync("users/root/verify-password", token, new { password = "root" }), 403);

        await AssertErrorAsync(await _h.GetAsync("sessions", token), 403);
        await AssertErrorAsync(await _h.GetAsync("audit", token), 403);
        await AssertErrorAsync(await _h.DeleteAsync("audit", token), 403);

        Assert.Equal(200, (int)(await _h.GetAsync("groups", token)).StatusCode);
        Assert.Equal(200, (int)(await _h.GetAsync("groups/admin", token)).StatusCode);
        Assert.Equal(200, (int)(await _h.GetAsync("groups/users/members", token)).StatusCode);
        await AssertErrorAsync(await _h.GetAsync("groups/admin/members", token), 403);
        await AssertErrorAsync(await _h.PostAsync("groups", token, new { name = "hackers" }), 403);
        await AssertErrorAsync(await _h.PutAsync("groups/users", token, new { description = "x" }), 403);
        await AssertErrorAsync(await _h.DeleteAsync("groups/users", token), 403);
        await AssertErrorAsync(await _h.PostAsync("groups/admin/members/plain-user", token), 403);
        await AssertErrorAsync(await _h.DeleteAsync("groups/users/members/plain-user", token), 403);
    }

    [Fact]
    public async Task SelfUpdate_OnlyEmailAndData()
    {
        await _h.CreateUserAsync(_root, "selfie");
        var token = (await _h.LoginAsync("selfie", "pass1234")).Token;

        var ok = await _h.PutAsync("users/selfie", token, new { email = "new-selfie@test.com", data = new { k = "v" } });
        Assert.Equal(200, (int)ok.StatusCode);
        Assert.Equal("new-selfie@test.com", (await ReadJsonAsync(ok)).GetProperty("email").GetString());

        // Unchanged restricted fields are tolerated.
        Assert.Equal(200, (int)(await _h.PutAsync("users/selfie", token, new { name = "selfie", groups = new[] { "users" } })).StatusCode);

        await AssertErrorAsync(await _h.PutAsync("users/selfie", token, new { name = "renamed" }), 403);
        await AssertErrorAsync(await _h.PutAsync("users/selfie", token, new { groups = new[] { "admin" } }), 403);
        await AssertErrorAsync(await _h.PutAsync("users/selfie", token, new { disabled = true }), 403);
        await AssertErrorAsync(await _h.PutAsync("users/selfie", token, new { homeDir = "/root" }), 403);
    }

    [Fact]
    public async Task CustomGroup_GrantsExactPermissions()
    {
        var auditor = await _h.UserWithPermissionsAsync(_root, "auditor", "audit:read", "USERS:READ");

        Assert.Equal(200, (int)(await _h.GetAsync("users", auditor)).StatusCode);
        Assert.Equal(200, (int)(await _h.GetAsync("users/root", auditor)).StatusCode);
        Assert.Equal(200, (int)(await _h.GetAsync("users/root/permissions", auditor)).StatusCode);
        Assert.Equal(200, (int)(await _h.GetAsync("audit", auditor)).StatusCode);

        await AssertErrorAsync(await _h.DeleteAsync("audit", auditor), 403);
        await AssertErrorAsync(await _h.PostAsync("users", auditor, new { name = "x", email = "x@x.x" }), 403);
        await AssertErrorAsync(await _h.PutAsync("users/root", auditor, new { email = "x@x.x" }), 403);
        await AssertErrorAsync(await _h.GetAsync("sessions", auditor), 403);
        await AssertErrorAsync(await _h.GetAsync("groups/admin/members", auditor), 403);
        await AssertErrorAsync(await _h.PostAsync("groups", auditor, new { name = "g" }), 403);
    }

    [Fact]
    public async Task WildcardGroup_GrantsResourceActions()
    {
        var helpdesk = await _h.UserWithPermissionsAsync(_root, "helpdesk", "users:*", "sessions:read");

        var created = await _h.PostAsync("users", helpdesk, new { name = "made-by-helpdesk", email = "m@h.d", password = "pass1234" });
        Assert.Equal(201, (int)created.StatusCode);
        Assert.Equal(200, (int)(await _h.PutAsync("users/made-by-helpdesk", helpdesk, new { email = "m2@h.d" })).StatusCode);
        Assert.Equal(204, (int)(await _h.PutAsync("users/made-by-helpdesk/password", helpdesk, new { password = "newpass1" })).StatusCode);
        Assert.Equal(200, (int)(await _h.GetAsync("sessions", helpdesk)).StatusCode);
        Assert.Equal(204, (int)(await _h.DeleteAsync("users/made-by-helpdesk", helpdesk)).StatusCode);

        var (target, ts) = await _h.LoginAsync("root", "root");
        await AssertErrorAsync(await _h.DeleteAsync($"sessions/{ts.GetProperty("id").GetString()}", helpdesk), 403);
        await AssertErrorAsync(await _h.PostAsync("groups", helpdesk, new { name = "g" }), 403);
        await AssertErrorAsync(await _h.GetAsync("audit", helpdesk), 403);
        Assert.NotNull(target);
    }

    [Fact]
    public async Task GroupsReadPermission_AllowsMembersListing()
    {
        var reader = await _h.UserWithPermissionsAsync(_root, "greader", "groups:read");
        var res = await _h.GetAsync("groups/admin/members", reader);
        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal(["root"], Names(await ReadJsonAsync(res)));
    }

    // =========================================================================
    // Passwords
    // =========================================================================

    [Fact]
    public async Task ChangePassword_ByAdmin_RevokesAllTargetSessions()
    {
        var created = await _h.CreateUserAsync(_root, "pw-target", "oldpass");
        var (token, _) = await _h.LoginAsync("pw-target", "oldpass");

        var res = await _h.PutAsync("users/pw-target/password", _root, new { password = "newpass" });
        Assert.Equal(204, (int)res.StatusCode);

        await AssertErrorAsync(await _h.GetAsync("sessions/current", token), 401);
        var kicked = Assert.Single(_h.Events.SessionKicked());
        Assert.Equal("password_changed", kicked.GetProperty("reason").GetString());
        Assert.Equal(created.GetProperty("id").GetString(), kicked.GetProperty("userId").GetString());

        await AssertErrorAsync(await _h.PostAsync("sessions", null, new { username = "pw-target", password = "oldpass" }), 401);
        await _h.LoginAsync("pw-target", "newpass");

        var audit = (await AuditAsync("event=password_changed")).Single();
        Assert.Equal("pw-target", audit.GetProperty("username").GetString());
        Assert.Equal("root", audit.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task ChangePassword_BySelf_RevokesOnlyOtherSessions()
    {
        await _h.CreateUserAsync(_root, "pw-self", "selfpass");
        var (t1, _) = await _h.LoginAsync("pw-self", "selfpass");
        var (t2, s2) = await _h.LoginAsync("pw-self", "selfpass");

        var res = await _h.PutAsync("users/pw-self/password", t1, new { currentPassword = "selfpass", password = "selfpass2" });
        Assert.Equal(204, (int)res.StatusCode);

        Assert.Equal(200, (int)(await _h.GetAsync("sessions/current", t1)).StatusCode);
        await AssertErrorAsync(await _h.GetAsync("sessions/current", t2), 401);
        var kicked = Assert.Single(_h.Events.SessionKicked());
        Assert.Equal(s2.GetProperty("id").GetString(), kicked.GetProperty("sessionId").GetString());
        await _h.LoginAsync("pw-self", "selfpass2");
    }

    [Fact]
    public async Task ChangePassword_SelfWrongCurrent_Returns403_MissingCurrent_Returns400()
    {
        await _h.CreateUserAsync(_root, "pw-wrong", "rightpass");
        var (token, _) = await _h.LoginAsync("pw-wrong", "rightpass");

        await AssertErrorAsync(await _h.PutAsync("users/pw-wrong/password", token, new { currentPassword = "nope", password = "newpass" }), 403);
        await AssertErrorAsync(await _h.PutAsync("users/pw-wrong/password", token, new { password = "newpass" }), 400);
    }

    [Fact]
    public async Task ChangePassword_SelfWithUsersUpdate_DoesNotNeedCurrentPassword()
    {
        var (other, _) = await _h.LoginAsync("root", "root");

        var res = await _h.PutAsync("users/root/password", _root, new { password = "newroot" });
        Assert.Equal(204, (int)res.StatusCode);

        // A wrong currentPassword is ignored for privileged callers too.
        Assert.Equal(204, (int)(await _h.PutAsync("users/root/password", _root,
            new { currentPassword = "wrong", password = "newroot2" })).StatusCode);

        Assert.Equal(200, (int)(await _h.GetAsync("sessions/current", _root)).StatusCode);
        await AssertErrorAsync(await _h.GetAsync("sessions/current", other), 401);
        await _h.LoginAsync("root", "newroot2");
    }

    [Fact]
    public async Task ChangePassword_SelfWithCustomUsersUpdateGroup_DoesNotNeedCurrentPassword()
    {
        var token = await _h.UserWithPermissionsAsync(_root, "pw-manager", "users:update");
        Assert.Equal(204, (int)(await _h.PutAsync("users/pw-manager/password", token, new { password = "changed1" })).StatusCode);
        await _h.LoginAsync("pw-manager", "changed1");
    }

    [Fact]
    public async Task ChangePassword_PolicyAndMissingField_Return400()
    {
        await _h.CreateUserAsync(_root, "pw-policy");
        await AssertErrorAsync(await _h.PutAsync("users/pw-policy/password", _root, new { password = "" }), 400);
        await AssertErrorAsync(await _h.PutAsync("users/pw-policy/password", _root, new { password = "abc" }), 400);
        await AssertErrorAsync(await _h.PutAsync("users/nobody/password", _root, new { password = "abcd" }), 404);
    }

    [Fact]
    public async Task VerifyPassword_ReturnsValidFlag()
    {
        var ok = await _h.PostAsync($"users/{_rootId}/verify-password", _root, new { password = "root" });
        Assert.Equal(200, (int)ok.StatusCode);
        Assert.True((await ReadJsonAsync(ok)).GetProperty("valid").GetBoolean());

        var bad = await _h.PostAsync("users/root/verify-password", _root, new { password = "wrong" });
        Assert.Equal(200, (int)bad.StatusCode);
        Assert.False((await ReadJsonAsync(bad)).GetProperty("valid").GetBoolean());

        var failed = (await AuditAsync("event=login_failed")).Single();
        Assert.Equal("root", failed.GetProperty("username").GetString());
    }

    [Fact]
    public async Task VerifyPassword_MissingPassword_Returns400_WithoutSession_Returns401()
    {
        await AssertErrorAsync(await _h.PostAsync("users/root/verify-password", _root, new { password = "" }), 400);
        await AssertErrorAsync(await _h.PostAsync("users/root/verify-password", null, new { password = "root" }), 401);
    }

    [Fact]
    public async Task VerifyPassword_FailuresCountTowardLockout()
    {
        await _h.CreateUserAsync(_root, "verifier");
        var (token, _) = await _h.LoginAsync("verifier", "pass1234");
        for (var i = 0; i < 5; i++)
        {
            var res = await _h.PostAsync("users/verifier/verify-password", token, new { password = "bad" });
            Assert.Equal(200, (int)res.StatusCode);
        }

        await AssertErrorAsync(await _h.PostAsync("users/verifier/verify-password", token, new { password = "pass1234" }), 429);
        await AssertErrorAsync(await _h.PostAsync("sessions", null, new { username = "verifier", password = "pass1234" }), 429);
    }

    // =========================================================================
    // Groups
    // =========================================================================

    [Fact]
    public async Task BuiltInGroups_AreSeeded()
    {
        var groups = (await ReadJsonAsync(await _h.GetAsync("groups", _root))).EnumerateArray().ToList();
        var admin = groups.Single(g => g.GetProperty("name").GetString() == "admin");
        Assert.Equal(["*"], Strings(admin.GetProperty("permissions")));
        Assert.True(admin.GetProperty("builtIn").GetBoolean());
        var users = groups.Single(g => g.GetProperty("name").GetString() == "users");
        Assert.Equal(0, users.GetProperty("permissions").GetArrayLength());
        Assert.True(users.GetProperty("builtIn").GetBoolean());
        foreach (var key in new[] { "id", "createdAt", "updatedAt" })
            Assert.True(admin.TryGetProperty(key, out _));
    }

    [Fact]
    public async Task CreateGroup_Returns201WithShape_AndDuplicateIsCaseInsensitive()
    {
        var res = await _h.PostAsync("groups", _root, new { name = "Editors", description = "Content editors", permissions = new[] { "docs:write" } });
        Assert.Equal(201, (int)res.StatusCode);
        var group = await ReadJsonAsync(res);
        Assert.Equal("Editors", group.GetProperty("name").GetString());
        Assert.Equal(["docs:write"], Strings(group.GetProperty("permissions")));
        Assert.False(group.GetProperty("builtIn").GetBoolean());

        await AssertErrorAsync(await _h.PostAsync("groups", _root, new { name = "editors" }), 409);
        await AssertErrorAsync(await _h.PostAsync("groups", _root, new { name = "" }), 400);
        await AssertErrorAsync(await _h.PostAsync("groups", _root, new { name = "x", permissions = new[] { " " } }), 400);

        var noDescription = await ReadJsonAsync(await _h.PostAsync("groups", _root, new { name = "bare" }));
        Assert.False(noDescription.TryGetProperty("description", out _));
        Assert.Equal(0, noDescription.GetProperty("permissions").GetArrayLength());
    }

    [Fact]
    public async Task GetGroup_IsCaseInsensitive_Unknown404()
    {
        var res = await _h.GetAsync("groups/ADMIN", _root);
        Assert.Equal("admin", (await ReadJsonAsync(res)).GetProperty("name").GetString());
        await AssertErrorAsync(await _h.GetAsync("groups/no-such-group", _root), 404);
        await AssertErrorAsync(await _h.GetAsync("groups/no-such-group/members", _root), 404);
    }

    [Fact]
    public async Task UpdateGroup_DescriptionAndPermissions()
    {
        await _h.CreateGroupAsync(_root, "ops-team", "users:read");
        var res = await _h.PutAsync("groups/OPS-TEAM", _root, new { description = "Ops", permissions = new[] { "users:*", "sessions:read" } });
        Assert.Equal(200, (int)res.StatusCode);
        var group = await ReadJsonAsync(res);
        Assert.Equal("ops-team", group.GetProperty("name").GetString());
        Assert.Equal("Ops", group.GetProperty("description").GetString());
        Assert.Equal(["users:*", "sessions:read"], Strings(group.GetProperty("permissions")));

        await AssertErrorAsync(await _h.PutAsync("groups/nope", _root, new { description = "x" }), 404);

        var audit = (await AuditAsync("event=group_updated")).Single();
        Assert.Equal("root", audit.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task DeleteGroup_RemovesItFromMembers()
    {
        await _h.CreateGroupAsync(_root, "temp");
        await _h.CreateUserAsync(_root, "temp-member", groups: ["users", "TEMP"]);

        Assert.Equal(204, (int)(await _h.DeleteAsync("groups/Temp", _root)).StatusCode);
        await AssertErrorAsync(await _h.GetAsync("groups/temp", _root), 404);
        var user = await ReadJsonAsync(await _h.GetAsync("users/temp-member", _root));
        Assert.Equal(["users"], Strings(user.GetProperty("groups")));
        await AssertErrorAsync(await _h.DeleteAsync("groups/temp", _root), 404);
    }

    [Fact]
    public async Task Members_AddAndRemove_ByNameOrId()
    {
        await _h.CreateGroupAsync(_root, "crew");
        var user = await _h.CreateUserAsync(_root, "member-user");
        var userId = user.GetProperty("id").GetString();

        Assert.Equal(204, (int)(await _h.PostAsync("groups/CREW/members/member-user", _root)).StatusCode);
        Assert.Equal(204, (int)(await _h.PostAsync("groups/crew/members/MEMBER-USER", _root)).StatusCode); // idempotent
        var after = await ReadJsonAsync(await _h.GetAsync("users/member-user", _root));
        Assert.Equal(["users", "crew"], Strings(after.GetProperty("groups")));

        var members = await ReadJsonAsync(await _h.GetAsync("groups/crew/members", _root));
        Assert.Equal(["member-user"], Names(members));

        Assert.Equal(204, (int)(await _h.DeleteAsync($"groups/crew/members/{userId}", _root)).StatusCode);
        Assert.Equal(204, (int)(await _h.PostAsync($"groups/crew/members/{userId}", _root)).StatusCode);
        Assert.Equal(204, (int)(await _h.DeleteAsync("groups/Crew/members/member-user", _root)).StatusCode);
        after = await ReadJsonAsync(await _h.GetAsync("users/member-user", _root));
        Assert.Equal(["users"], Strings(after.GetProperty("groups")));

        var events = (await AuditAsync("username=member-user")).Select(e => e.GetProperty("event").GetString()).ToList();
        Assert.Equal(2, events.Count(e => e == "group_member_added"));
        Assert.Equal(2, events.Count(e => e == "group_member_removed"));
    }

    [Fact]
    public async Task AddMember_UnknownGroup_Returns400_UnknownUser_Returns404()
    {
        await _h.CreateUserAsync(_root, "lonely");
        Assert.Equal("unknown group: nonexistent",
            await AssertErrorAsync(await _h.PostAsync("groups/nonexistent/members/lonely", _root), 400));
        await AssertErrorAsync(await _h.PostAsync("groups/users/members/no-such-user", _root), 404);
        await AssertErrorAsync(await _h.DeleteAsync("groups/users/members/no-such-user", _root), 404);
        await AssertErrorAsync(await _h.DeleteAsync("groups/nonexistent/members/lonely", _root), 404);
    }

    // =========================================================================
    // Audit
    // =========================================================================

    [Fact]
    public async Task Audit_RecordsAllEventsWithActor()
    {
        await _h.CreateGroupAsync(_root, "evt-group", "x:y");
        await _h.CreateUserAsync(_root, "evt-user");
        await _h.PutAsync("groups/evt-group", _root, new { description = "d" });
        await _h.PostAsync("groups/evt-group/members/evt-user", _root);
        await _h.DeleteAsync("groups/evt-group/members/evt-user", _root);
        await _h.PutAsync("users/evt-user", _root, new { disabled = true });
        await _h.PutAsync("users/evt-user", _root, new { disabled = false });
        await _h.PutAsync("users/evt-user/password", _root, new { password = "newpass" });
        var (t, s) = await _h.LoginAsync("evt-user", "newpass");
        await _h.DeleteAsync($"sessions/{s.GetProperty("id").GetString()}", _root);
        await _h.PostAsync("sessions", null, new { username = "evt-user", password = "bad" });
        var (t2, _) = await _h.LoginAsync("evt-user", "newpass");
        await _h.DeleteAsync("sessions/current", t2);
        await _h.DeleteAsync("users/evt-user", _root);
        await _h.DeleteAsync("groups/evt-group", _root);
        Assert.NotNull(t);

        var entries = await AuditAsync();
        var events = entries.Select(e => e.GetProperty("event").GetString()).ToHashSet();
        foreach (var expected in new[]
                 {
                     "login", "login_failed", "logout", "user_created", "user_updated", "user_deleted",
                     "user_disabled", "user_enabled", "password_changed", "group_created", "group_updated",
                     "group_deleted", "group_member_added", "group_member_removed", "session_revoked",
                 })
        {
            Assert.Contains(expected, events);
        }

        foreach (var e in entries)
        {
            Assert.False(string.IsNullOrEmpty(e.GetProperty("id").GetString()));
            Assert.True(e.GetProperty("timestamp").GetInt64() > 0);
            Assert.True(e.TryGetProperty("username", out _));
        }

        foreach (var name in new[] { "user_created", "user_deleted", "group_created", "group_member_added", "session_revoked" })
            Assert.Equal("root", entries.First(e => e.GetProperty("event").GetString() == name).GetProperty("actor").GetString());

        // Newest first.
        var stamps = entries.Select(e => e.GetProperty("timestamp").GetInt64()).ToList();
        Assert.Equal(stamps.OrderByDescending(x => x), stamps);
    }

    [Fact]
    public async Task Audit_FiltersAndLimit()
    {
        await _h.LoginAsync("root", "root");
        await _h.PostAsync("sessions", null, new { username = "root", password = "bad" });

        var logins = await AuditAsync("event=login");
        Assert.Equal(2, logins.Count);
        Assert.All(logins, e => Assert.Equal("login", e.GetProperty("event").GetString()));

        Assert.Single(await AuditAsync("event=login&limit=1"));
        Assert.All(await AuditAsync("username=ROOT"), e => Assert.Equal("root", e.GetProperty("username").GetString()));
        await AssertErrorAsync(await _h.GetAsync("audit?limit=-1", _root), 400);
        await AssertErrorAsync(await _h.GetAsync("audit?limit=x", _root), 400);
    }

    [Fact]
    public async Task Audit_WithoutAuth_Returns401()
    {
        await AssertErrorAsync(await _h.GetAsync("audit"), 401);
    }

    [Fact]
    public async Task ClearAudit_LeavesOnlyAuditClearedEntry()
    {
        Assert.Equal(204, (int)(await _h.DeleteAsync("audit", _root)).StatusCode);
        var entry = Assert.Single(await AuditAsync());
        Assert.Equal("audit_cleared", entry.GetProperty("event").GetString());
        Assert.Equal("root", entry.GetProperty("actor").GetString());
    }

    // =========================================================================
    // Host configuration independence & fail-closed
    // =========================================================================

    [Fact]
    public async Task Responses_AreCamelCaseAndOmitNulls_RegardlessOfHostJsonOptions()
    {
        await using var host = await UsersTestHost.StartAsync(jsonOptions: o =>
        {
            o.JsonSerializerOptions.PropertyNamingPolicy = null;
            o.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault;
        });
        var token = await host.RootTokenAsync();
        var created = await ReadJsonAsync(await host.PostAsync("users", token, new { name = "shape", email = "s@s.s" }));
        Assert.True(created.TryGetProperty("name", out _));
        Assert.False(created.GetProperty("disabled").GetBoolean());
        Assert.False(created.TryGetProperty("homeDir", out _));
        Assert.False(created.TryGetProperty("Name", out _));

        var group = await ReadJsonAsync(await host.GetAsync("groups/users", token));
        Assert.True(group.GetProperty("builtIn").GetBoolean());
        Assert.Equal(0, group.GetProperty("permissions").GetArrayLength());
    }

    [Fact]
    public async Task WithoutMiddleware_EndpointsFailClosed()
    {
        await using var host = await UsersTestHost.StartAsync(useMiddleware: false);

        // Login still works (anonymous), but the token is never accepted without the middleware.
        var (token, _) = await host.LoginAsync("root", "root");

        foreach (var path in new[] { "users", "users/root", "groups", "groups/admin", "sessions", "sessions/current", "audit" })
        {
            await AssertErrorAsync(await host.GetAsync(path), 401);
            await AssertErrorAsync(await host.GetAsync(path, token), 401);
        }
        await AssertErrorAsync(await host.PostAsync("users", token, new { name = "x", email = "x@x.x" }), 401);
        await AssertErrorAsync(await host.DeleteAsync("users/root", token), 401);
        await AssertErrorAsync(await host.DeleteAsync("audit", token), 401);
    }

    [Fact]
    public async Task PasswordsAreStoredAsPbkdf2()
    {
        var store = _h.Service<Qodalis.Cli.Plugin.Users.Stores.ICredentialStore>();
        var creds = await store.GetCredentialsAsync(_rootId);
        Assert.NotNull(creds);
        var parts = creds!.PasswordHash.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2", parts[0]);
        Assert.Equal("100000", parts[1]);
        Assert.Equal(16, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);
        Assert.True(PasswordHasher.Verify("root", creds.PasswordHash));
    }
}
