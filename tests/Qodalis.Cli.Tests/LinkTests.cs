using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Plugin.Users;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Security;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// Link requests: a terminal asks to be linked, a signed-in user approves it in a browser
/// (spec: web-cli docs/superpowers/specs/2026-11-12-server-linking-design.md; Node: src/__tests__/server-link.test.ts).
/// </summary>
public class LinkHelperTests
{
    [Fact]
    public void NamesTokensFromTheClientName()
    {
        Assert.Equal("link Qodalis CLI on Pixel 7", LinkService.TokenName("Qodalis CLI on Pixel 7"));
        Assert.Equal("link caf--terminal", LinkService.TokenName("café/terminal"));
        Assert.Equal(64, LinkService.TokenName(new string('x', 100)).Length);
        Assert.Equal("link Pixel 2", LinkService.TokenName("Pixel", 1));
        Assert.Equal(64, LinkService.TokenName(new string('x', 100), 1).Length);
        Assert.EndsWith(" 2", LinkService.TokenName(new string('x', 100), 1));
    }

    [Fact]
    public void BuildsTheVerificationUrl()
    {
        Assert.Equal("/qcli/admin/link?code=ABCD-2345", LinkService.VerificationUrl("/qcli/admin/link", "ABCD-2345"));
        Assert.Equal("https://app.example.com/link/ABCD-2345",
            LinkService.VerificationUrl("https://app.example.com/link/{code}", "ABCD-2345"));
        Assert.Equal("/approve?x=1&code=ABCD-2345", LinkService.VerificationUrl("/approve?x=1", "ABCD-2345"));
    }

    [Fact]
    public void NormalisesCodes()
    {
        Assert.Equal("ABCD2345", LinkService.NormalizeUserCode(" abcd-2345 "));
    }

    [Fact]
    public void ValidatesTheApprovalUrl()
    {
        var options = new UsersModuleOptions();
        Assert.Null(options.LinkApprovalUrl);
        options.LinkApprovalUrl = "https://app.example.com/cli/link/{code}";
        options.LinkApprovalUrl = "/approve";
        Assert.Throws<ArgumentException>(() => options.LinkApprovalUrl = "javascript:alert(1)");
        Assert.Throws<ArgumentException>(() => options.LinkApprovalUrl = "//evil.example.com");
        Assert.Throws<ArgumentException>(() => options.LinkApprovalUrl = "/\\evil.example.com");
        Assert.Throws<ArgumentException>(() => options.LinkApprovalUrl = "relative/path");
        Assert.Equal("/approve", options.LinkApprovalUrl);
    }

    [Theory]
    [InlineData("POST", "/api/v1/qcli/link", true)]
    [InlineData("GET", "/api/v1/qcli/link/abc_DEF-123", true)]
    [InlineData("GET", "/api/v1/qcli/link/code/ABCD-2345", false)]
    [InlineData("GET", "/api/v1/qcli/link/code", false)]
    [InlineData("POST", "/api/v1/qcli/link/code/ABCD-2345/approve", false)]
    [InlineData("POST", "/api/v1/qcli/link/code/ABCD-2345/deny", false)]
    [InlineData("DELETE", "/api/v1/qcli/link/abc", false)]
    [InlineData("GET", "/api/v1/qcli/link", false)]
    public void PublicLinkRoutes(string method, string path, bool isPublic)
    {
        Assert.Equal(isPublic, CliRoutePolicy.Resolve(method, path).IsPublic);
    }
}

public class LinkRoutesTests : IAsyncLifetime
{
    private const string V1 = UsersTestHost.Base;
    private const string Client = "Qodalis CLI on Pixel 7";
    private const string Origin = "https://cli.qodalis.com";

    private UsersTestHost _h = null!;
    private string _root = null!;
    private string _ann = null!;
    private int _address;

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync(o => o.TrustProxy = true);
        _root = await _h.RootTokenAsync();
        await _h.CreateUserAsync(_root, "ann", "annpass12", email: "ann@x.io");
        _ann = (await _h.LoginAsync("ann", "annpass12")).Token;
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<HttpResponseMessage> PostLinkAsync(object body, string? forwardedFor = null, string? origin = Origin)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{V1}/link")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (origin != null) req.Headers.Add("Origin", origin);
        req.Headers.Add("X-Forwarded-For", forwardedFor ?? $"10.0.0.{++_address}");
        return _h.Client.SendAsync(req);
    }

    private async Task<JsonElement> CreateAsync(string client = Client, string[]? permissions = null)
    {
        object body = permissions == null ? new { client } : new { client, permissions };
        var res = await PostLinkAsync(body);
        Assert.Equal(201, (int)res.StatusCode);
        return await UsersTestHost.ReadJsonAsync(res);
    }

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private async Task<List<JsonElement>> TokensAsync(string token) =>
        (await UsersTestHost.ReadJsonAsync(await _h.GetAsync("tokens", token))).EnumerateArray().ToList();

    [Fact]
    public async Task CreatesARequestWithoutSigningIn()
    {
        var body = await CreateAsync();
        Assert.Equal(600, body.GetProperty("expiresIn").GetInt32());
        Assert.Equal(2, body.GetProperty("interval").GetInt32());
        Assert.Matches("^[A-Za-z0-9_-]{43}$", Str(body, "linkId"));
        var code = Str(body, "userCode");
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", code);
        Assert.Equal($"/qcli/admin/link?code={code}", Str(body, "verificationUrl"));

        var poll = await UsersTestHost.ReadJsonAsync(await _h.GetAsync($"link/{Str(body, "linkId")}"));
        Assert.Equal("""{"status":"pending"}""", poll.GetRawText());
    }

    [Fact]
    public async Task RejectsInvalidBodies()
    {
        await UsersTestHost.AssertErrorAsync(await PostLinkAsync(new { }), 400);
        await UsersTestHost.AssertErrorAsync(await PostLinkAsync(new { client = new string('x', 101) }), 400);
        await UsersTestHost.AssertErrorAsync(await PostLinkAsync(new { client = "a", permissions = Array.Empty<string>() }), 400);
        await UsersTestHost.AssertErrorAsync(await PostLinkAsync(new { client = "a", permissions = new[] { 1 } }), 400);
        await UsersTestHost.AssertErrorAsync(await PostLinkAsync(new { client = "a\u0001b" }), 400);
        await UsersTestHost.AssertErrorAsync(await PostLinkAsync(new { client = 5 }), 400);
        await UsersTestHost.AssertErrorAsync(await PostLinkAsync(new { client = "a", permissions = new[] { "bad permission!" } }), 400);
    }

    [Fact]
    public async Task LimitsRequestsPerClientAddress()
    {
        for (var i = 0; i < 10; i++)
            Assert.Equal(201, (int)(await PostLinkAsync(new { client = "spam" }, "192.0.2.7")).StatusCode);
        var limited = await PostLinkAsync(new { client = "spam" }, "192.0.2.7");
        await UsersTestHost.AssertErrorAsync(limited, 429);
        // Another address is not limited, and the window slides
        Assert.Equal(201, (int)(await PostLinkAsync(new { client = "other" }, "192.0.2.8")).StatusCode);
        _h.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(201, (int)(await PostLinkAsync(new { client = "spam" }, "192.0.2.7")).StatusCode);
    }

    [Fact]
    public async Task ApprovalFlow_ThePageReadsTheRequest_ApprovalHandsTheTokenOutOnce()
    {
        var created = await CreateAsync();
        var linkId = Str(created, "linkId");
        var userCode = Str(created, "userCode");
        await UsersTestHost.AssertErrorAsync(await _h.GetAsync($"link/code/{userCode}"), 401);

        var view = await _h.GetAsync($"link/code/{userCode.ToLowerInvariant().Replace("-", "")}", _ann);
        Assert.Equal(200, (int)view.StatusCode);
        var page = await UsersTestHost.ReadJsonAsync(view);
        Assert.Equal(userCode, Str(page, "userCode"));
        Assert.Equal(Client, Str(page, "client"));
        Assert.Equal(Origin, Str(page, "origin"));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("permissions").ValueKind);
        Assert.StartsWith("10.0.0.", Str(page, "remoteAddress"));
        Assert.True(page.GetProperty("expiresAt").GetInt64() - page.GetProperty("createdAt").GetInt64() == 600_000);

        var approved = await _h.PostAsync($"link/code/{userCode}/approve", _ann);
        Assert.Equal(200, (int)approved.StatusCode);
        var approval = await UsersTestHost.ReadJsonAsync(approved);
        Assert.Equal("link Qodalis CLI on Pixel 7", Str(approval, "tokenName"));

        var pollRes = await _h.GetAsync($"link/{linkId}");
        Assert.Equal(200, (int)pollRes.StatusCode);
        Assert.Equal("no-store", pollRes.Headers.CacheControl?.ToString());
        var poll = await UsersTestHost.ReadJsonAsync(pollRes);
        Assert.Equal("approved", Str(poll, "status"));
        Assert.Equal("ann", Str(poll, "userName"));
        var token = Str(poll, "token");
        Assert.StartsWith("qcli_pat_", token);
        Assert.Equal(_h.Time.GetUtcNow().ToUnixTimeMilliseconds() + 90L * 24 * 60 * 60 * 1000,
            poll.GetProperty("expiresAt").GetInt64());

        var again = await _h.GetAsync($"link/{linkId}");
        Assert.Equal(404, (int)again.StatusCode);
        Assert.Equal("""{"status":"expired"}""", (await UsersTestHost.ReadJsonAsync(again)).GetRawText());

        // The token works and is listed with ann's tokens
        Assert.Equal(200, (int)(await _h.GetAsync($"{V1}/commands", token)).StatusCode);
        var tokens = await TokensAsync(_ann);
        var listed = tokens.Single(t => Str(t, "name") == "link Qodalis CLI on Pixel 7");
        Assert.Equal(Str(approval, "tokenId"), Str(listed, "id"));

        // Single use
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync($"link/code/{userCode}/approve", _ann), 404);

        var entries = await _h.Service<IAuditStore>().GetEntriesAsync(new AuditQueryOptions { Event = AuditEvents.LinkApproved });
        var entry = Assert.Single(entries);
        Assert.Equal("ann", entry.Username);
        Assert.Equal("ann", entry.Actor);
        Assert.Equal("Qodalis CLI on Pixel 7 (link Qodalis CLI on Pixel 7)", entry.Details);
        Assert.Equal(Client, entry.Metadata?.Client);
        Assert.Equal(Origin, entry.Metadata?.Origin);
        Assert.Equal(Str(approval, "tokenId"), entry.Metadata?.TokenId);
        Assert.Single(await _h.Service<IAuditStore>().GetEntriesAsync(new AuditQueryOptions { Event = AuditEvents.TokenCreated, Username = "ann" }));
    }

    [Fact]
    public async Task ASecondTerminalWithTheSameNameGetsANumberedToken()
    {
        var first = await CreateAsync();
        Assert.Equal(200, (int)(await _h.PostAsync($"link/code/{Str(first, "userCode")}/approve", _ann)).StatusCode);
        var second = await CreateAsync();
        var approved = await _h.PostAsync($"link/code/{Str(second, "userCode")}/approve", _ann);
        Assert.Equal("link Qodalis CLI on Pixel 7 2", Str(await UsersTestHost.ReadJsonAsync(approved), "tokenName"));
    }

    [Fact]
    public async Task ParallelApprovals_OnlyOneCreatesAToken()
    {
        var created = await CreateAsync("parallel");
        var userCode = Str(created, "userCode");
        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => _h.PostAsync($"link/code/{userCode}/approve", _ann)));
        Assert.Single(results, r => (int)r.StatusCode == 200);
        Assert.Equal(4, results.Count(r => (int)r.StatusCode == 404));
        Assert.Single(await TokensAsync(_ann), t => Str(t, "name").StartsWith("link parallel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Denial_TheTerminalLearnsItOnce_NoTokenIsCreated()
    {
        var created = await CreateAsync("denied term");
        var linkId = Str(created, "linkId");
        var userCode = Str(created, "userCode");
        Assert.Equal(204, (int)(await _h.PostAsync($"link/code/{userCode}/deny", _ann)).StatusCode);
        Assert.Equal("""{"status":"denied"}""", (await UsersTestHost.ReadJsonAsync(await _h.GetAsync($"link/{linkId}"))).GetRawText());
        Assert.Equal(404, (int)(await _h.GetAsync($"link/{linkId}")).StatusCode);
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync($"link/code/{userCode}/approve", _ann), 404);
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync($"link/code/{userCode}/deny", _ann), 404);
        Assert.DoesNotContain(await TokensAsync(_ann), t => Str(t, "name") == "link denied term");

        var entry = Assert.Single(await _h.Service<IAuditStore>().GetEntriesAsync(new AuditQueryOptions { Event = AuditEvents.LinkDenied }));
        Assert.Equal("ann", entry.Username);
        Assert.Equal("denied term", entry.Metadata?.Client);
        Assert.Equal(Origin, entry.Metadata?.Origin);
    }

    [Fact]
    public async Task ScopedRequests_TheTokenCarriesThePermissions_NeverMoreThanTheApproverHas()
    {
        var scoped = await CreateAsync("scoped", ["commands:execute"]);
        var page = await UsersTestHost.ReadJsonAsync(await _h.GetAsync($"link/code/{Str(scoped, "userCode")}", _root));
        Assert.Equal("""["commands:execute"]""", page.GetProperty("permissions").GetRawText());
        Assert.Equal(200, (int)(await _h.PostAsync($"link/code/{Str(scoped, "userCode")}/approve", _root)).StatusCode);
        var listed = (await TokensAsync(_root)).Single(t => Str(t, "name") == "link scoped");
        Assert.Equal("""["commands:execute"]""", listed.GetProperty("permissions").GetRawText());

        var greedy = await CreateAsync("greedy", ["users:write"]);
        var error = await UsersTestHost.AssertErrorAsync(await _h.PostAsync($"link/code/{Str(greedy, "userCode")}/approve", _ann), 403);
        Assert.Contains("users:write", error);
        // A failed approval leaves the request pending for another approver
        Assert.Equal(200, (int)(await _h.PostAsync($"link/code/{Str(greedy, "userCode")}/approve", _root)).StatusCode);
    }

    [Fact]
    public async Task ApiTokensCannotApprove()
    {
        var first = await CreateAsync("via token");
        Assert.Equal(200, (int)(await _h.PostAsync($"link/code/{Str(first, "userCode")}/approve", _ann)).StatusCode);
        var token = Str(await UsersTestHost.ReadJsonAsync(await _h.GetAsync($"link/{Str(first, "linkId")}")), "token");
        var second = await CreateAsync("second");
        var error = await UsersTestHost.AssertErrorAsync(await _h.PostAsync($"link/code/{Str(second, "userCode")}/approve", token), 403);
        Assert.Equal(AuthService.ApiTokensCannotCreateTokensMessage, error);
    }

    [Fact]
    public async Task UnknownCodesAndLinks()
    {
        await UsersTestHost.AssertErrorAsync(await _h.GetAsync("link/code/AAAA-AAAA", _ann), 404);
        var res = await _h.GetAsync("link/nope");
        Assert.Equal(404, (int)res.StatusCode);
        Assert.Equal("""{"status":"expired"}""", (await UsersTestHost.ReadJsonAsync(res)).GetRawText());
    }

    [Fact]
    public async Task ExpiresRequestsAfterTenMinutes()
    {
        var created = await CreateAsync();
        var links = _h.Service<LinkService>();
        Assert.Equal(1, links.Count);
        _h.Time.Advance(TimeSpan.FromSeconds(LinkService.ExpiresInSeconds));
        Assert.Equal(404, (int)(await _h.GetAsync($"link/{Str(created, "linkId")}")).StatusCode);
        await UsersTestHost.AssertErrorAsync(await _h.GetAsync($"link/code/{Str(created, "userCode")}", _ann), 404);

        // The cleanup sweep drops expired requests without a request
        await CreateAsync();
        _h.Time.Advance(TimeSpan.FromSeconds(LinkService.ExpiresInSeconds));
        links.Sweep();
        Assert.Equal(0, links.Count);
    }

    [Fact]
    public async Task KeepsAtMost100PendingRequests_DroppingTheOldest()
    {
        var first = await CreateAsync("first");
        for (var i = 0; i < LinkService.MaxPendingLinks; i++)
            await CreateAsync($"t{i}");
        Assert.Equal(LinkService.MaxPendingLinks, _h.Service<LinkService>().Count);
        Assert.Equal(404, (int)(await _h.GetAsync($"link/{Str(first, "linkId")}")).StatusCode);
    }
}

public class LinkHostOptionsTests
{
    [Fact]
    public async Task UsesTheHostPage()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.LinkApprovalUrl = "https://app.example.com/cli/link/{code}");
        var res = await h.PostAsync("link", null, new { client = "x" });
        Assert.Equal(201, (int)res.StatusCode);
        var body = await UsersTestHost.ReadJsonAsync(res);
        Assert.Equal($"https://app.example.com/cli/link/{body.GetProperty("userCode").GetString()}",
            body.GetProperty("verificationUrl").GetString());
    }

    /// <summary>A token store that runs a callback while a token is created.</summary>
    private sealed class SlowTokenStore(Action onCreate) : IApiTokenStore
    {
        private readonly InMemoryApiTokenStore _inner = new();

        public Task<ApiToken> CreateTokenAsync(ApiToken token)
        {
            onCreate();
            return _inner.CreateTokenAsync(token);
        }

        public Task<ApiToken?> GetTokenByHashAsync(string tokenHash) => _inner.GetTokenByHashAsync(tokenHash);
        public Task<ApiToken?> GetTokenAsync(string id) => _inner.GetTokenAsync(id);
        public Task<List<ApiToken>> GetTokensAsync(string? userId = null) => _inner.GetTokensAsync(userId);
        public Task DeleteTokenAsync(string id) => _inner.DeleteTokenAsync(id);
        public Task DeleteUserTokensAsync(string userId) => _inner.DeleteUserTokensAsync(userId);
        public Task UpdateLastUsedAsync(string id, long lastUsedAt) => _inner.UpdateLastUsedAsync(id, lastUsedAt);
    }

    [Fact]
    public async Task ARequestThatExpiresDuringTheApproval_RevokesTheNewToken()
    {
        UsersTestHost? host = null;
        var expireOnCreate = false;
        await using var h = host = await UsersTestHost.StartAsync(o => o.SetApiTokenStore(_ => new SlowTokenStore(() =>
        {
            if (expireOnCreate) host!.Time.Advance(TimeSpan.FromSeconds(LinkService.ExpiresInSeconds));
        })));
        var root = await h.RootTokenAsync();
        var created = await UsersTestHost.ReadJsonAsync(await h.PostAsync("link", null, new { client = "late" }));
        var code = created.GetProperty("userCode").GetString();

        expireOnCreate = true;
        await UsersTestHost.AssertErrorAsync(await h.PostAsync($"link/code/{code}/approve", root), 404);
        expireOnCreate = false;

        var tokens = (await UsersTestHost.ReadJsonAsync(await h.GetAsync("tokens", root))).EnumerateArray();
        Assert.DoesNotContain(tokens, t => t.GetProperty("name").GetString() == "link late");
        var audit = h.Service<IAuditStore>();
        Assert.Empty(await audit.GetEntriesAsync(new AuditQueryOptions { Event = AuditEvents.LinkApproved }));
        Assert.Single(await audit.GetEntriesAsync(new AuditQueryOptions { Event = AuditEvents.TokenRevoked }));
        Assert.Equal(404, (int)(await h.GetAsync($"link/{created.GetProperty("linkId").GetString()}")).StatusCode);
    }

    [Fact]
    public async Task ApproveAnswers501WithAnExternalAuthAdapter()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.UseExternalAuth(_ => new ExternalAuthResult
        {
            User = new User { Id = "ext-ann", Name = "ann", Email = "ann@external.com" },
            IsAdmin = true,
        }));
        Task<HttpResponseMessage> PostAsync(string path)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{UsersTestHost.Base}/{path}")
            {
                Content = new StringContent(path == "link" ? """{"client":"x"}""" : "{}", Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("Origin", "http://localhost"); // same origin: adapter auth needs it on writes
            return h.Client.SendAsync(req);
        }

        var created = await UsersTestHost.ReadJsonAsync(await PostAsync("link"));
        var code = created.GetProperty("userCode").GetString();
        Assert.Equal(200, (int)(await h.GetAsync($"link/code/{code}")).StatusCode);
        await UsersTestHost.AssertErrorAsync(await PostAsync($"link/code/{code}/approve"), 501);
        Assert.Equal(204, (int)(await PostAsync($"link/code/{code}/deny")).StatusCode);
    }
}
