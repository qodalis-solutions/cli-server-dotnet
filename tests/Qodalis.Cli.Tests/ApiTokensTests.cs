using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// API tokens of the users plugin: <c>/api/v1/qcli/tokens</c> (create, list, revoke), bearer
/// authentication with <c>qcli_pat_…</c> tokens, scopes, expiry, limits, lifecycle and audit.
/// </summary>
public class ApiTokensTests : IAsyncLifetime
{
    private UsersTestHost _h = null!;
    private string _root = null!;

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync();
        _root = await _h.RootTokenAsync();
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private static object Exec(string command) => new { command, value = "", args = new { }, chainCommands = Array.Empty<string>() };

    private async Task<(string Token, JsonElement ApiToken)> CreateTokenAsync(string sessionToken, object body, int status = 201)
    {
        var res = await _h.PostAsync("tokens", sessionToken, body);
        Assert.Equal(status, (int)res.StatusCode);
        var json = await UsersTestHost.ReadJsonAsync(res);
        return (json.GetProperty("token").GetString()!, json.GetProperty("apiToken"));
    }

    private async Task<string> PlainUserAsync(string name)
    {
        await _h.CreateUserAsync(_root, name);
        return (await _h.LoginAsync(name, "pass1234")).Token;
    }

    // -------------------------------------------------------------------------
    // Create
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Create_ReturnsTokenOnceAndView()
    {
        var before = _h.Time.GetUtcNow().ToUnixTimeMilliseconds();
        var res = await _h.PostAsync("tokens", _root, new { name = "ci token" });
        Assert.Equal(201, (int)res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(text);

        var token = body.GetProperty("token").GetString()!;
        Assert.StartsWith("qcli_pat_", token);
        var raw = token["qcli_pat_".Length..];
        Assert.Equal(43, raw.Length); // 32 bytes, base64url without padding
        Assert.DoesNotContain('=', raw);
        Assert.DoesNotContain('+', raw);
        Assert.DoesNotContain('/', raw);

        var view = body.GetProperty("apiToken");
        Assert.Equal("ci token", view.GetProperty("name").GetString());
        Assert.Equal("root", view.GetProperty("userName").GetString());
        Assert.False(string.IsNullOrEmpty(view.GetProperty("id").GetString()));
        Assert.False(string.IsNullOrEmpty(view.GetProperty("userId").GetString()));
        Assert.Equal(before, view.GetProperty("createdAt").GetInt64());
        Assert.Equal(before + 90L * 24 * 60 * 60 * 1000, view.GetProperty("expiresAt").GetInt64());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("lastUsedAt").ValueKind);
        Assert.False(view.TryGetProperty("permissions", out _));
        Assert.False(view.TryGetProperty("tokenHash", out _));
        Assert.DoesNotContain(AuthService.HashToken(token), text);
    }

    [Fact]
    public async Task Create_StoresOnlyTheHash()
    {
        var (token, view) = await CreateTokenAsync(_root, new { name = "hashed" });
        var stored = await _h.Service<IApiTokenStore>().GetTokenAsync(view.GetProperty("id").GetString()!);
        Assert.NotNull(stored);
        Assert.Equal(AuthService.HashToken(token), stored!.TokenHash);
        Assert.NotEqual(token, stored.TokenHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad/name")]
    [InlineData("emojié")]
    [InlineData("tab\tname")]
    public async Task Create_InvalidName_Returns400(string name)
    {
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync("tokens", _root, new { name }), 400);
    }

    [Fact]
    public async Task Create_NameTooLong_Returns400()
    {
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync("tokens", _root, new { name = new string('a', 65) }), 400);
        await CreateTokenAsync(_root, new { name = new string('a', 64) });
    }

    [Fact]
    public async Task Create_MissingName_Returns400()
    {
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync("tokens", _root, new { }), 400);
    }

    [Fact]
    public async Task Create_DuplicateName_CaseInsensitive_Returns409()
    {
        await CreateTokenAsync(_root, new { name = "Deploy" });
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync("tokens", _root, new { name = "deploy" }), 409);

        // Unique per user: another user may use the same name.
        var other = await PlainUserAsync("dup-other");
        await CreateTokenAsync(other, new { name = "deploy" });
    }

    [Theory]
    [InlineData("0")]
    [InlineData("366")]
    [InlineData("1.5")]
    [InlineData("-3")]
    [InlineData("\"30\"")]
    [InlineData("true")]
    public async Task Create_InvalidExpiresInDays_Returns400(string value)
    {
        var res = await _h.PostAsync("tokens", _root, $"{{\"name\":\"exp\",\"expiresInDays\":{value}}}");
        await UsersTestHost.AssertErrorAsync(res, 400);
    }

    [Fact]
    public async Task Create_ExpiresInDays_SetsExpiry()
    {
        var now = _h.Time.GetUtcNow().ToUnixTimeMilliseconds();
        var (_, view) = await CreateTokenAsync(_root, new { name = "short", expiresInDays = 1 });
        Assert.Equal(now + 24L * 60 * 60 * 1000, view.GetProperty("expiresAt").GetInt64());
        var (_, longView) = await CreateTokenAsync(_root, new { name = "long", expiresInDays = 365 });
        Assert.Equal(now + 365L * 24 * 60 * 60 * 1000, longView.GetProperty("expiresAt").GetInt64());
    }

    [Fact]
    public async Task Create_NonExpiring_RejectedByDefault()
    {
        var res = await _h.PostAsync("tokens", _root, "{\"name\":\"forever\",\"expiresInDays\":null}");
        Assert.Contains("non-expiring", await UsersTestHost.AssertErrorAsync(res, 400));
    }

    [Fact]
    public async Task Create_NonExpiring_AllowedWithOption()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.AllowNonExpiringTokens = true);
        var root = await h.RootTokenAsync();
        var res = await h.PostAsync("tokens", root, "{\"name\":\"forever\",\"expiresInDays\":null}");
        Assert.Equal(201, (int)res.StatusCode);
        var body = await UsersTestHost.ReadJsonAsync(res);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("apiToken").GetProperty("expiresAt").ValueKind);

        // Still valid far in the future.
        h.Time.Advance(TimeSpan.FromDays(3650));
        var token = body.GetProperty("token").GetString()!;
        Assert.Equal(200, (int)(await h.PostAsync("execute", token, Exec("unknown"))).StatusCode);
    }

    [Fact]
    public async Task Create_TwentyTokenLimit()
    {
        for (var i = 0; i < AuthService.MaxApiTokensPerUser; i++)
            await CreateTokenAsync(_root, new { name = $"t{i}" });

        var error = await UsersTestHost.AssertErrorAsync(await _h.PostAsync("tokens", _root, new { name = "t20" }), 400);
        Assert.Contains("20", error);

        // Revoking one frees a slot.
        var list = await UsersTestHost.ReadJsonAsync(await _h.GetAsync("tokens", _root));
        Assert.Equal(204, (int)(await _h.DeleteAsync($"tokens/{list[0].GetProperty("id").GetString()}", _root)).StatusCode);
        await CreateTokenAsync(_root, new { name = "t20" });
    }

    [Fact]
    public async Task Create_ExpiredTokensDoNotCountTowardTheLimit()
    {
        for (var i = 0; i < AuthService.MaxApiTokensPerUser; i++)
            await CreateTokenAsync(_root, new { name = $"old{i}", expiresInDays = 1 });
        _h.Time.Advance(TimeSpan.FromDays(2));
        await CreateTokenAsync(await _h.RootTokenAsync(), new { name = "fresh" });
    }

    [Fact]
    public async Task Create_WithApiToken_Returns403()
    {
        var (token, _) = await CreateTokenAsync(_root, new { name = "minter" });
        var error = await UsersTestHost.AssertErrorAsync(await _h.PostAsync("tokens", token, new { name = "child" }), 403);
        Assert.Equal("forbidden: API tokens cannot create tokens", error);
    }

    [Fact]
    public async Task Create_Unauthenticated_Returns401()
    {
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync("tokens", null, new { name = "anon" }), 401);
    }

    [Fact]
    public async Task Create_ExternalAuth_Returns501()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.UseExternalAuth(_ => new ExternalAuthResult
        {
            User = new User { Id = "ext-1", Name = "ext", Email = "ext@test.com" },
            IsAdmin = true,
        }));
        var req = new HttpRequestMessage(HttpMethod.Post, $"{UsersTestHost.Base}/tokens")
        {
            Content = new StringContent("{\"name\":\"ext\"}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Origin", "http://localhost");
        var res = await h.Client.SendAsync(req);
        Assert.Equal(501, (int)res.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Scope
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Create_ScopeNotHeld_Returns403()
    {
        var user = await _h.UserWithPermissionsAsync(_root, "scoper", "commands:execute");
        var error = await UsersTestHost.AssertErrorAsync(
            await _h.PostAsync("tokens", user, new { name = "s", permissions = new[] { "users:read" } }), 403);
        Assert.Equal("forbidden: cannot grant users:read", error);
    }

    [Fact]
    public async Task Create_InvalidPermission_Returns400()
    {
        await UsersTestHost.AssertErrorAsync(
            await _h.PostAsync("tokens", _root, new { name = "s", permissions = new[] { "bad perm!" } }), 400);
    }

    [Fact]
    public async Task Scope_IntersectsWithUserPermissions()
    {
        var user = await _h.UserWithPermissionsAsync(_root, "scoped", "commands:execute", "users:read");
        var (token, view) = await CreateTokenAsync(user, new { name = "read-only", permissions = new[] { "users:read" } });
        Assert.Equal(["users:read"], view.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray());

        // users:read is in scope, commands:execute is not.
        Assert.Equal(200, (int)(await _h.GetAsync("users", token)).StatusCode);
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync("execute", token, Exec("echo")), 403);

        // The session (unscoped) still executes.
        Assert.Equal(200, (int)(await _h.PostAsync("execute", user, Exec("unknown"))).StatusCode);

        // The user loses users:read: the scope entry no longer applies.
        Assert.Equal(200, (int)(await _h.PutAsync("groups/scoped-group", _root,
            new { permissions = new[] { "commands:execute" } })).StatusCode);
        await UsersTestHost.AssertErrorAsync(await _h.GetAsync("users", token), 403);
    }

    [Fact]
    public async Task Unscoped_UsesLivePermissions()
    {
        var user = await _h.UserWithPermissionsAsync(_root, "live", "commands:execute");
        var (token, _) = await CreateTokenAsync(user, new { name = "live" });
        await UsersTestHost.AssertErrorAsync(await _h.GetAsync("users", token), 403);

        Assert.Equal(200, (int)(await _h.PutAsync("groups/live-group", _root,
            new { permissions = new[] { "commands:execute", "users:read" } })).StatusCode);
        Assert.Equal(200, (int)(await _h.GetAsync("users", token)).StatusCode);
    }

    // -------------------------------------------------------------------------
    // Authentication
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Token_AuthenticatesAndRecordsLastUse()
    {
        var (token, view) = await CreateTokenAsync(_root, new { name = "use" });
        var id = view.GetProperty("id").GetString()!;

        Assert.Equal(200, (int)(await _h.PostAsync("execute", token, Exec("unknown"))).StatusCode);
        var first = (await _h.Service<IApiTokenStore>().GetTokenAsync(id))!.LastUsedAt;
        Assert.Equal(_h.Time.GetUtcNow().ToUnixTimeMilliseconds(), first);

        // At most once per minute.
        _h.Time.Advance(TimeSpan.FromSeconds(30));
        await _h.PostAsync("execute", token, Exec("unknown"));
        Assert.Equal(first, (await _h.Service<IApiTokenStore>().GetTokenAsync(id))!.LastUsedAt);

        _h.Time.Advance(TimeSpan.FromSeconds(31));
        await _h.PostAsync("execute", token, Exec("unknown"));
        Assert.Equal(_h.Time.GetUtcNow().ToUnixTimeMilliseconds(), (await _h.Service<IApiTokenStore>().GetTokenAsync(id))!.LastUsedAt);

        var list = await UsersTestHost.ReadJsonAsync(await _h.GetAsync("tokens", _root));
        Assert.Equal(JsonValueKind.Number, list.EnumerateArray().Single(t => t.GetProperty("id").GetString() == id)
            .GetProperty("lastUsedAt").ValueKind);
    }

    [Fact]
    public async Task Token_OutlivesSessionTimeout()
    {
        var (token, _) = await CreateTokenAsync(_root, new { name = "long-lived" });
        _h.Time.Advance(TimeSpan.FromDays(10));
        Assert.Equal(200, (int)(await _h.PostAsync("execute", token, Exec("unknown"))).StatusCode);
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync("execute", _root, Exec("unknown")), 401);
    }

    [Fact]
    public async Task UnknownToken_Returns401()
    {
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync("execute", "qcli_pat_doesnotexist", Exec("x")), 401);
    }

    [Fact]
    public async Task ExpiredToken_Returns401()
    {
        var (token, _) = await CreateTokenAsync(_root, new { name = "expiring", expiresInDays = 1 });
        _h.Time.Advance(TimeSpan.FromHours(23));
        Assert.Equal(200, (int)(await _h.PostAsync("execute", token, Exec("unknown"))).StatusCode);
        _h.Time.Advance(TimeSpan.FromHours(1));
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync("execute", token, Exec("unknown")), 401);
    }

    [Fact]
    public async Task RevokedToken_Returns401()
    {
        var (token, view) = await CreateTokenAsync(_root, new { name = "revoke-me" });
        Assert.Equal(200, (int)(await _h.PostAsync("execute", token, Exec("unknown"))).StatusCode);
        Assert.Equal(204, (int)(await _h.DeleteAsync($"tokens/{view.GetProperty("id").GetString()}", _root)).StatusCode);
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync("execute", token, Exec("unknown")), 401);
    }

    [Fact]
    public async Task DisabledUser_TokenFails_UntilReEnabled()
    {
        var user = await PlainUserAsync("disable-me");
        var (token, _) = await CreateTokenAsync(user, new { name = "t" });
        Assert.Equal(200, (int)(await _h.PostAsync("execute", token, Exec("unknown"))).StatusCode);

        Assert.Equal(200, (int)(await _h.PutAsync("users/disable-me", _root, new { disabled = true })).StatusCode);
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync("execute", token, Exec("unknown")), 401);

        // Not deleted: works again once the user is re-enabled.
        Assert.Equal(200, (int)(await _h.PutAsync("users/disable-me", _root, new { disabled = false })).StatusCode);
        Assert.Equal(200, (int)(await _h.PostAsync("execute", token, Exec("unknown"))).StatusCode);
    }

    [Fact]
    public async Task MustChangePassword_TokenGets403()
    {
        var user = await PlainUserAsync("must-change");
        var (token, _) = await CreateTokenAsync(user, new { name = "t" });
        Assert.Equal(204, (int)(await _h.PutAsync("users/must-change/password", _root,
            new { password = "newpass123" })).StatusCode);

        var error = await UsersTestHost.AssertErrorAsync(await _h.PostAsync("execute", token, Exec("unknown")), 403);
        Assert.Equal(AuthService.PasswordChangeRequiredMessage, error);
    }

    [Fact]
    public async Task Token_CannotChangePassword()
    {
        var user = await PlainUserAsync("pw-token");
        var (token, _) = await CreateTokenAsync(user, new { name = "t" });
        var error = await UsersTestHost.AssertErrorAsync(await _h.PutAsync("users/pw-token/password", token,
            new { password = "another123", currentPassword = "pass1234" }), 403);
        Assert.Equal("forbidden: API tokens cannot change passwords", error);

        // Not even an admin token for another user.
        var (rootToken, _) = await CreateTokenAsync(_root, new { name = "admin" });
        await UsersTestHost.AssertErrorAsync(await _h.PutAsync("users/pw-token/password", rootToken,
            new { password = "another123" }), 403);
    }

    [Fact]
    public async Task CurrentSessionEndpoints_WithToken_Return404()
    {
        var (token, _) = await CreateTokenAsync(_root, new { name = "not-session" });
        Assert.Equal("not a session", await UsersTestHost.AssertErrorAsync(await _h.GetAsync("sessions/current", token), 404));
        Assert.Equal("not a session", await UsersTestHost.AssertErrorAsync(await _h.DeleteAsync("sessions/current", token), 404));

        // The token still works afterwards.
        Assert.Equal(200, (int)(await _h.PostAsync("execute", token, Exec("unknown"))).StatusCode);
    }

    // -------------------------------------------------------------------------
    // List
    // -------------------------------------------------------------------------

    [Fact]
    public async Task List_OwnTokens()
    {
        var user = await PlainUserAsync("lister");
        await CreateTokenAsync(user, new { name = "mine" });
        await CreateTokenAsync(_root, new { name = "roots" });

        var list = await UsersTestHost.ReadJsonAsync(await _h.GetAsync("tokens", user));
        var entry = Assert.Single(list.EnumerateArray());
        Assert.Equal("mine", entry.GetProperty("name").GetString());
        Assert.Equal("lister", entry.GetProperty("userName").GetString());
        Assert.False(entry.TryGetProperty("tokenHash", out _));
        Assert.False(entry.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task List_WithToken_ListsOwnTokens()
    {
        var (token, _) = await CreateTokenAsync(_root, new { name = "a" });
        await CreateTokenAsync(_root, new { name = "b" });
        var list = await UsersTestHost.ReadJsonAsync(await _h.GetAsync("tokens", token));
        Assert.Equal(["a", "b"], list.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray());
    }

    [Fact]
    public async Task ListAll_RequiresTokensRead()
    {
        var user = await PlainUserAsync("no-read");
        await CreateTokenAsync(user, new { name = "u" });
        await CreateTokenAsync(_root, new { name = "r" });

        await UsersTestHost.AssertErrorAsync(await _h.GetAsync("tokens?all=true", user), 403);

        var reader = await _h.UserWithPermissionsAsync(_root, "reader", "tokens:read");
        var all = await UsersTestHost.ReadJsonAsync(await _h.GetAsync("tokens?all=true", reader));
        var names = all.EnumerateArray().Select(t => $"{t.GetProperty("userName").GetString()}/{t.GetProperty("name").GetString()}").ToList();
        Assert.Contains("no-read/u", names);
        Assert.Contains("root/r", names);

        // Admin gets tokens:read through "*".
        Assert.Equal(200, (int)(await _h.GetAsync("tokens?all=true", _root)).StatusCode);
    }

    [Fact]
    public async Task List_InvalidAllValue_Returns400()
    {
        await UsersTestHost.AssertErrorAsync(await _h.GetAsync("tokens?all=yes", _root), 400);
    }

    // -------------------------------------------------------------------------
    // Revoke
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Revoke_Own_Returns204_AndAudits()
    {
        var user = await PlainUserAsync("revoker");
        var (_, view) = await CreateTokenAsync(user, new { name = "audit-me" });
        var id = view.GetProperty("id").GetString()!;
        Assert.Equal(204, (int)(await _h.DeleteAsync($"tokens/{id}", user)).StatusCode);
        Assert.Empty((await UsersTestHost.ReadJsonAsync(await _h.GetAsync("tokens", user))).EnumerateArray());

        var audit = await _h.Service<IAuditStore>().GetEntriesAsync();
        var created = audit.Single(e => e.Event == AuditEvents.TokenCreated && e.Metadata?.TokenId == id);
        Assert.Equal("revoker", created.Actor);
        Assert.Contains("audit-me", created.Details);
        Assert.Contains(id, created.Details);
        var revoked = audit.Single(e => e.Event == AuditEvents.TokenRevoked && e.Metadata?.TokenId == id);
        Assert.Equal("revoker", revoked.Actor);
        Assert.Equal("audit-me", revoked.Metadata!.TokenName);
    }

    [Fact]
    public async Task Revoke_WithTheTokenItself_Works()
    {
        var (token, view) = await CreateTokenAsync(_root, new { name = "self-revoke" });
        Assert.Equal(204, (int)(await _h.DeleteAsync($"tokens/{view.GetProperty("id").GetString()}", token)).StatusCode);
        await UsersTestHost.AssertErrorAsync(await _h.GetAsync("tokens", token), 401);
    }

    [Fact]
    public async Task Revoke_OthersToken_WithoutPermission_Returns404()
    {
        var owner = await PlainUserAsync("owner");
        var (token, view) = await CreateTokenAsync(owner, new { name = "theirs" });
        var other = await PlainUserAsync("intruder");

        await UsersTestHost.AssertErrorAsync(await _h.DeleteAsync($"tokens/{view.GetProperty("id").GetString()}", other), 404);
        await UsersTestHost.AssertErrorAsync(await _h.DeleteAsync("tokens/does-not-exist", other), 404);
        Assert.Equal(200, (int)(await _h.PostAsync("execute", token, Exec("unknown"))).StatusCode);
    }

    [Fact]
    public async Task Revoke_OthersToken_WithTokensRevoke()
    {
        var owner = await PlainUserAsync("owner2");
        var (token, view) = await CreateTokenAsync(owner, new { name = "theirs" });
        var revoker = await _h.UserWithPermissionsAsync(_root, "token-admin", "tokens:revoke");

        Assert.Equal(204, (int)(await _h.DeleteAsync($"tokens/{view.GetProperty("id").GetString()}", revoker)).StatusCode);
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync("execute", token, Exec("unknown")), 401);
        await UsersTestHost.AssertErrorAsync(await _h.DeleteAsync("tokens/does-not-exist", revoker), 404);

        var audit = await _h.Service<IAuditStore>().GetEntriesAsync(new AuditQueryOptions { Event = AuditEvents.TokenRevoked });
        var entry = Assert.Single(audit);
        Assert.Equal("owner2", entry.Username);
        Assert.Equal("token-admin", entry.Actor);
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DeletingUser_DeletesTokens()
    {
        var user = await PlainUserAsync("doomed");
        var (token, _) = await CreateTokenAsync(user, new { name = "t1" });
        await CreateTokenAsync(user, new { name = "t2" });

        Assert.Equal(204, (int)(await _h.DeleteAsync("users/doomed", _root)).StatusCode);
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync("execute", token, Exec("unknown")), 401);

        var all = await UsersTestHost.ReadJsonAsync(await _h.GetAsync("tokens?all=true", _root));
        Assert.DoesNotContain(all.EnumerateArray(), t => t.GetProperty("userName").GetString() == "doomed");
        Assert.DoesNotContain(await _h.Service<IApiTokenStore>().GetTokensAsync(), t => t.Name is "t1" or "t2");
    }

    [Fact]
    public async Task IsApiToken_RecognisesThePrefix()
    {
        Assert.False(AuthService.IsApiToken(_root));
        var (token, _) = await CreateTokenAsync(_root, new { name = "prefix" });
        Assert.True(AuthService.IsApiToken(token));
    }
}

/// <summary>Sockets opened with an API token identity are closed (4401) when the token ends.</summary>
public class ApiTokenSocketTests : IAsyncLifetime
{
    private UsersTestHost _h = null!;
    private string _root = null!;

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync(realEventSockets: true);
        _root = await _h.RootTokenAsync();
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private async Task<(string Token, string Id)> TokenAsync(string name)
    {
        var res = await _h.PostAsync("tokens", _root, new { name });
        Assert.Equal(201, (int)res.StatusCode);
        var body = await UsersTestHost.ReadJsonAsync(res);
        return (body.GetProperty("token").GetString()!, body.GetProperty("apiToken").GetProperty("id").GetString()!);
    }

    private static async Task<(int Code, string? Reason)> ReceiveUntilCloseAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(5000);
        var buffer = new byte[8192];
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                return ((int)result.CloseStatus!.Value, result.CloseStatusDescription);
        }
    }

    private async Task<WebSocket> OpenEventsAsync(string token)
    {
        var socket = await _h.ConnectWebSocketAsync($"/ws/v1/qcli/events?ticket={await _h.TicketAsync(token)}");
        Assert.Contains("connected", await UsersTestHost.ReceiveTextAsync(socket));
        return socket;
    }

    [Fact]
    public async Task EventsSocket_TokenRevoked_Closes4401()
    {
        var (token, id) = await TokenAsync("socket");
        var socket = await OpenEventsAsync(token);

        Assert.Equal(204, (int)(await _h.DeleteAsync($"tokens/{id}", _root)).StatusCode);

        var (code, reason) = await ReceiveUntilCloseAsync(socket);
        Assert.Equal(4401, code);
        Assert.Equal("session ended", reason);
        socket.Dispose();
    }

    [Fact]
    public async Task EventsSocket_UserDisabled_Closes4401()
    {
        await _h.CreateUserAsync(_root, "sock-user");
        var session = (await _h.LoginAsync("sock-user", "pass1234")).Token;
        var res = await _h.PostAsync("tokens", session, new { name = "t" });
        var token = (await UsersTestHost.ReadJsonAsync(res)).GetProperty("token").GetString()!;
        // The session ends with the disable too; log it out first so only the token identity remains.
        Assert.Equal(204, (int)(await _h.DeleteAsync("sessions/current", session)).StatusCode);

        var socket = await OpenEventsAsync(token);
        Assert.Equal(200, (int)(await _h.PutAsync("users/sock-user", _root, new { disabled = true })).StatusCode);

        var (code, _) = await ReceiveUntilCloseAsync(socket);
        Assert.Equal(4401, code);
        socket.Dispose();
    }

    [Fact]
    public async Task EventsSocket_OtherTokenRevoked_StaysOpen()
    {
        var (token, _) = await TokenAsync("keep");
        var (_, otherId) = await TokenAsync("drop");
        var socket = await OpenEventsAsync(token);

        Assert.Equal(204, (int)(await _h.DeleteAsync($"tokens/{otherId}", _root)).StatusCode);
        await Task.Delay(100);
        await _h.Service<Qodalis.Cli.Services.ICliEventSocketManager>().BroadcastMessageAsync("{\"type\":\"marker\"}");
        Assert.Contains("marker", await UsersTestHost.ReceiveTextAsync(socket));
        await UsersTestHost.CloseAsync(socket);
    }

    [Fact]
    public async Task Ticket_FromRevokedToken_IsRejected()
    {
        var (token, id) = await TokenAsync("ticket");
        var ticket = await _h.TicketAsync(token);
        Assert.Equal(204, (int)(await _h.DeleteAsync($"tokens/{id}", _root)).StatusCode);
        Assert.Equal(401, await _h.RejectedUpgradeStatusAsync($"/ws/v1/qcli/events?ticket={ticket}"));
    }
}
