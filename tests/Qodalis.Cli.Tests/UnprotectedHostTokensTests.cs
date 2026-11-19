using Microsoft.AspNetCore.Builder;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Plugin.Users.Authorization;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Security;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// <c>host</c> tokens on a server that is not protected (<c>ProtectServer</c> off): CLI routes, sockets and plugin
/// routes still refuse them (403), while anonymous and <c>cli</c>-token requests behave as before.
/// </summary>
public class UnprotectedHostTokensTests : IAsyncLifetime
{
    private const string RefusedText = "forbidden: host tokens cannot use CLI routes";
    private const string Refused = """{"error":"forbidden: host tokens cannot use CLI routes"}""";

    private UsersTestHost _h = null!;
    private string _aliceId = null!;
    private string _host = null!;
    private string _cli = null!;

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync(o => o.ProtectServer = false,
            configureCli: cli =>
            {
                cli.AddPermissions(p => p.Group("Rates").Add("rates:read", "Official exchange rates"));
                cli.AddProcessor(new TestProcessor("echo", "Echo"));
                cli.AddMcp();
                cli.AddFileSystem(o => o.UseInMemory());
                cli.AddDataExplorerProvider(new FakeDataExplorerProvider(), o => o.Name = "db");
                cli.EnableShell(o =>
                {
                    o.Shell = "/bin/sh";
                    o.IdleTimeout = null;
                });
                cli.AddAdmin(o =>
                {
                    o.Username = "admin";
                    o.Password = "admin-pass";
                    o.JwtSecret = "unprotected-host-token-test-secret-long-enough-for-hmac";
                });
            },
            configureApp: app =>
            {
                app.UseQodalisAdmin();
                app.UseEndpoints(e => e.MapGet("/host/rates", () => "ok").RequireQcliAuthorization("rates:read"));
            });
        var root = await _h.RootTokenAsync();
        await _h.CreateGroupAsync(root, "alice-group", "rates:read", "*");
        _aliceId = (await _h.CreateUserAsync(root, "alice", groups: ["alice-group"])).GetProperty("id").GetString()!;
        var tokens = _h.Service<IApiTokenManager>();
        _host = (await tokens.CreateAsync(_aliceId, new ApiTokenCreateOptions
        {
            Name = "site", Audience = ApiTokenAudience.Host, Permissions = ["rates:read"],
        })).Token;
        _cli = (await tokens.CreateAsync(_aliceId, new ApiTokenCreateOptions { Name = "cli" })).Token;
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    /// <summary>Method, path, and whether the route authenticates callers itself even on an unprotected server.</summary>
    public static readonly TheoryData<string, string, bool> CliRoutes = new()
    {
        { "GET", "/api/v1/qcli/commands", false },
        { "POST", "/api/v1/qcli/execute", false },
        { "GET", "/api/qcli/fs/ls?path=/", false },
        { "POST", "/api/qcli/fs/mkdir", false },
        { "GET", "/api/qcli/data-explorer/sources", false },
        { "GET", "/api/v1/qcli/jobs", false },
        { "POST", "/api/qcli/mcp", false },
        { "GET", "/api/v1/qcli/audit", true },
        { "GET", "/api/v1/qcli/status", true },
        { "GET", "/api/v1/qcli/plugins", true },
        { "GET", "/api/v1/qcli/auth/me", true },
    };

    private Task<HttpResponseMessage> SendAsync(string method, string path, string? token) =>
        _h.SendAsync(new HttpMethod(method), path, token, method == "POST" ? new { command = "echo" } : null);

    [Theory]
    [MemberData(nameof(CliRoutes))]
    public async Task HostToken_RefusedOnCliRoutes_AnonymousAndCliTokenUnchanged(string method, string path, bool selfAuthenticated)
    {
        var host = await SendAsync(method, path, _host);
        Assert.Equal(403, (int)host.StatusCode);
        Assert.Equal(Refused, await host.Content.ReadAsStringAsync());

        var anonymous = await SendAsync(method, path, null);
        var cli = await SendAsync(method, path, _cli);
        if (selfAuthenticated)
        {
            // Audit and admin routes authenticate through the users plugin themselves: 401 anonymous, 200 with a cli token.
            Assert.Equal(401, (int)anonymous.StatusCode);
            Assert.Equal(200, (int)cli.StatusCode);
        }
        else
        {
            // Unprotected: a cli token is ignored on CLI routes, exactly like no credentials at all.
            Assert.Equal(anonymous.StatusCode, cli.StatusCode);
            Assert.NotEqual(403, (int)anonymous.StatusCode);
        }
    }

    [Fact]
    public async Task HostToken_StillAcceptedByTheGuard()
    {
        Assert.Equal(200, (int)(await _h.GetAsync("/host/rates", _host)).StatusCode);
    }

    [Fact]
    public async Task HostToken_RefusedOnTheEventsAndLogsSockets()
    {
        foreach (var path in new[] { "/ws/v1/qcli/events", "/ws/v1/qcli/logs" })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ShellTestClient.ConnectAsync(_h.Host, path, r => r.Headers.Authorization = $"Bearer {_host}"));
            Assert.Contains("403", ex.Message);
        }

        // Control: the events socket still opens anonymously and with a cli token on an unprotected server.
        await UsersTestHost.CloseAsync(await ShellTestClient.ConnectAsync(_h.Host, "/ws/v1/qcli/events"));
        await UsersTestHost.CloseAsync(await ShellTestClient.ConnectAsync(_h.Host, "/ws/v1/qcli/events",
            r => r.Headers.Authorization = $"Bearer {_cli}"));
    }

    [Fact]
    public async Task HostToken_ShellClosesWith4403()
    {
        var shell = await ShellTestClient.CloseOfAsync(_h.Host, "/ws/v1/qcli/shell?cmd=echo%20pwned",
            r => r.Headers.Authorization = $"Bearer {_host}");

        Assert.Equal(4403, shell.Code);
        Assert.Equal(RefusedText, shell.Reason);
        Assert.False(shell.GotReady);
    }

    [Fact]
    public async Task Refusal_DoesNotRecordTheTokensUse()
    {
        var store = _h.Service<IApiTokenStore>();
        var id = (await store.GetTokensAsync(_aliceId)).Single(t => t.Name == "site").Id;

        Assert.Equal(403, (int)(await _h.GetAsync("/api/v1/qcli/commands", _host)).StatusCode);

        Assert.Null((await store.GetTokenAsync(id))!.LastUsedAt);
    }

    [Fact]
    public async Task OnlyApiTokenBearersAreChecked()
    {
        // A bearer that is not an API token is left to the route, as before.
        var res = await _h.GetAsync("/api/v1/qcli/commands", "not-a-token");
        Assert.NotEqual(403, (int)res.StatusCode);
    }
}

/// <summary>The unprotected refusal reads the token store only: an external adapter is never called for it.</summary>
public class UnprotectedHostTokensAdapterTests
{
    [Theory]
    [InlineData("host")]
    [InlineData("HOST")]
    [InlineData("api")]
    public async Task HostToken_RefusedWithoutCallingTheAdapter_AnyNonCliAudience(string audience)
    {
        var calls = 0;
        await using var h = await UsersTestHost.StartAsync(o =>
            {
                o.ProtectServer = false;
                o.UseExternalAuth(_ =>
                {
                    Interlocked.Increment(ref calls);
                    return null;
                });
                o.AllowApiTokens();
                o.ApiTokenStore = new RespellingTokenStore(audience);
            },
            configureCli: cli =>
            {
                cli.AddPermissions(p => p.Group("Rates").Add("rates:read", "Official exchange rates"));
                cli.AddProcessor(new TestProcessor("echo", "Echo"));
            });
        await h.Service<IGroupStore>().CreateGroupAsync(new Group { Name = "rates-group", Permissions = ["rates:read"] });
        var ann = await h.Service<IUserStore>().CreateUserAsync(new CreateUserDto { Name = "ann", Email = "ann@x.com", Groups = ["rates-group"] });
        var host = (await h.Service<IApiTokenManager>().CreateAsync(ann.Id, new ApiTokenCreateOptions
        {
            Name = "site", Audience = ApiTokenAudience.Host, Permissions = ["rates:read"],
        })).Token;
        Interlocked.Exchange(ref calls, 0);

        var res = await h.GetAsync("/api/v1/qcli/commands", host);

        Assert.Equal(403, (int)res.StatusCode);
        Assert.Equal("""{"error":"forbidden: host tokens cannot use CLI routes"}""", await res.Content.ReadAsStringAsync());
        Assert.Equal(0, calls);
    }
}

/// <summary>Counts hash lookups, and can fail them (a store that is down).</summary>
internal sealed class CountingTokenStore(bool throwOnLookup = false) : IApiTokenStore
{
    private readonly InMemoryApiTokenStore _inner = new();
    public int HashLookups;
    public bool ThrowOnLookup { get; set; } = throwOnLookup;

    public Task<ApiToken> CreateTokenAsync(ApiToken token) => _inner.CreateTokenAsync(token);
    public Task<ApiToken?> GetTokenByHashAsync(string tokenHash)
    {
        Interlocked.Increment(ref HashLookups);
        if (ThrowOnLookup) throw new InvalidOperationException("token store is down");
        return _inner.GetTokenByHashAsync(tokenHash);
    }
    public Task<ApiToken?> GetTokenAsync(string id) => _inner.GetTokenAsync(id);
    public Task<List<ApiToken>> GetTokensAsync(string? userId = null) => _inner.GetTokensAsync(userId);
    public Task DeleteTokenAsync(string id) => _inner.DeleteTokenAsync(id);
    public Task DeleteUserTokensAsync(string userId) => _inner.DeleteUserTokensAsync(userId);
    public Task UpdateLastUsedAsync(string id, long lastUsedAt) => _inner.UpdateLastUsedAsync(id, lastUsedAt);
}

/// <summary>Edge cases of the host-token check: public routes, one store read, a failing store, the bearer scheme.</summary>
public class HostTokenCheckEdgeTests
{
    private static async Task<(UsersTestHost H, string Host, string Cli, string AliceId)> StartAsync(bool protect, IApiTokenStore? store = null)
    {
        var h = await UsersTestHost.StartAsync(o =>
            {
                o.ProtectServer = protect;
                if (store != null) o.ApiTokenStore = store;
            },
            configureCli: cli =>
            {
                cli.AddPermissions(p => p.Group("Rates").Add("rates:read", "Official exchange rates"));
                cli.AddProcessor(new TestProcessor("echo", "Echo"));
            });
        var root = await h.RootTokenAsync();
        await h.CreateGroupAsync(root, "alice-group", "rates:read", "commands:execute");
        var aliceId = (await h.CreateUserAsync(root, "alice", groups: ["alice-group"])).GetProperty("id").GetString()!;
        var tokens = h.Service<IApiTokenManager>();
        var host = (await tokens.CreateAsync(aliceId, new ApiTokenCreateOptions
        {
            Name = "site", Audience = ApiTokenAudience.Host, Permissions = ["rates:read"],
        })).Token;
        var cli = (await tokens.CreateAsync(aliceId, new ApiTokenCreateOptions { Name = "cli" })).Token;
        return (h, host, cli, aliceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicUsersRoutes_DoNotRefuseAHostBearer(bool protect)
    {
        var (h, host, _, _) = await StartAsync(protect);
        await using var _h = h;

        // Sign-in with a host bearer header: the credentials in the body decide.
        var login = await h.SendAsync(HttpMethod.Post, "sessions", host, new { username = "alice", password = "pass1234" });
        Assert.Equal(201, (int)login.StatusCode);

        // A terminal asks to be linked, then polls: both public.
        var link = await h.SendAsync(HttpMethod.Post, "link", host, new { });
        Assert.NotEqual(403, (int)link.StatusCode);
        Assert.DoesNotContain("host tokens", await link.Content.ReadAsStringAsync());
        var poll = await h.GetAsync("link/unknown-link-id", host);
        Assert.NotEqual(403, (int)poll.StatusCode);
        Assert.DoesNotContain("host tokens", await poll.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProtectedServer_ReadsTheTokenOncePerRequest()
    {
        var store = new CountingTokenStore();
        var (h, host, cli, _) = await StartAsync(protect: true, store);
        await using var _h = h;

        Interlocked.Exchange(ref store.HashLookups, 0);
        Assert.Equal(200, (int)(await h.GetAsync("/api/v1/qcli/commands", cli)).StatusCode);
        Assert.Equal(1, store.HashLookups);

        Interlocked.Exchange(ref store.HashLookups, 0);
        Assert.Equal(403, (int)(await h.GetAsync("/api/v1/qcli/commands", host)).StatusCode);
        Assert.Equal(1, store.HashLookups);
    }

    [Fact]
    public async Task UnprotectedServer_AFailingStoreLetsTheRequestThrough()
    {
        var store = new CountingTokenStore();
        var (h, host, _, _) = await StartAsync(protect: false, store);
        await using var _h = h;
        store.ThrowOnLookup = true;

        var res = await h.GetAsync("/api/v1/qcli/commands", host);

        Assert.Equal(200, (int)res.StatusCode);
    }

    [Theory]
    [InlineData("bearer")]
    [InlineData("BEARER")]
    public async Task TheCheckAcceptsAnyBearerSchemeCase_LikeTheAuthenticator(string scheme)
    {
        foreach (var protect in new[] { false, true })
        {
            var (h, host, cli, _) = await StartAsync(protect);
            await using var _h = h;

            var hostReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/qcli/commands");
            hostReq.Headers.TryAddWithoutValidation("Authorization", $"{scheme} {host}");
            var refused = await h.Client.SendAsync(hostReq);
            Assert.Equal(403, (int)refused.StatusCode);
            Assert.Contains("host tokens cannot use CLI routes", await refused.Content.ReadAsStringAsync());

            // The authenticator accepts the same scheme spelling for a cli token.
            var cliReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/qcli/tokens");
            cliReq.Headers.TryAddWithoutValidation("Authorization", $"{scheme} {cli}");
            Assert.Equal(200, (int)(await h.Client.SendAsync(cliReq)).StatusCode);
        }
    }
}
