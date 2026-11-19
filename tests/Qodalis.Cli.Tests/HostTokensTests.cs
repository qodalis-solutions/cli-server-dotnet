using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Users.Authorization;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Security;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>Host route guarded with <see cref="QcliAuthorizeAttribute"/> for <c>host</c> tokens.</summary>
[ApiController]
[Route("host/mvc-rates")]
public class HostRatesController : ControllerBase
{
    [HttpGet]
    [QcliAuthorize("rates:read")]
    public IActionResult Get() => Ok(new { ok = true });
}

/// <summary>Counts the catalog reads (the guards must not consult the catalog).</summary>
internal sealed class CountingPermissionCatalog(ICliPermissionCatalog inner) : ICliPermissionCatalog
{
    public int Reads;

    public IReadOnlyList<CliPermission> All
    {
        get
        {
            Interlocked.Increment(ref Reads);
            return inner.All;
        }
    }

    public CliPermission? Find(string name)
    {
        Interlocked.Increment(ref Reads);
        return inner.Find(name);
    }
}

/// <summary>A store that hands back the <c>host</c> audience spelled <paramref name="audience"/> (a custom store's data).</summary>
internal sealed class RespellingTokenStore(string audience) : IApiTokenStore
{
    private readonly InMemoryApiTokenStore _inner = new();

    private ApiToken? Respell(ApiToken? token)
    {
        if (token?.Audience == ApiTokenAudience.Host) token.Audience = audience;
        return token;
    }

    public Task<ApiToken> CreateTokenAsync(ApiToken token) => _inner.CreateTokenAsync(token);
    public async Task<ApiToken?> GetTokenByHashAsync(string tokenHash) => Respell(await _inner.GetTokenByHashAsync(tokenHash));
    public async Task<ApiToken?> GetTokenAsync(string id) => Respell(await _inner.GetTokenAsync(id));
    public async Task<List<ApiToken>> GetTokensAsync(string? userId = null) =>
        (await _inner.GetTokensAsync(userId)).Select(t => Respell(t)!).ToList();
    public Task DeleteTokenAsync(string id) => _inner.DeleteTokenAsync(id);
    public Task DeleteUserTokensAsync(string userId) => _inner.DeleteUserTokensAsync(userId);
    public Task UpdateLastUsedAsync(string id, long lastUsedAt) => _inner.UpdateLastUsedAsync(id, lastUsedAt);
}

/// <summary>
/// <c>host</c> API tokens: refused on every route the users plugin authenticates (users routes, CLI routes with
/// <c>ProtectServer</c>, the auth bridge) and accepted only by the host's <c>QcliAuthorize</c> guards.
/// </summary>
public class HostTokensTests : IAsyncLifetime
{
    private const string Refused = """{"error":"forbidden: host tokens cannot use CLI routes"}""";

    private UsersTestHost _h = null!;
    private string _root = null!;
    private string _aliceId = null!;
    private string _host = null!;
    private CountingPermissionCatalog _catalog = null!;

    private static void Rates(CliBuilder cli)
    {
        cli.AddPermissions(p => p.Group("Rates")
            .Add("rates:read", "Official exchange rates")
            .Add("banks:read", "Banks and their buy and sell rates"));
        cli.AddProcessor(new TestProcessor("echo", "Echo"));
        cli.AddMcp();
    }

    private static void HostRoutes(IApplicationBuilder app) =>
        app.UseEndpoints(e => e.MapGet("/host/rates", () => "ok").RequireQcliAuthorization("rates:read"));

    /// <summary>Wraps the catalog registration with <see cref="CountingPermissionCatalog"/>.</summary>
    private void CountCatalogReads(IMvcBuilder mvc)
    {
        mvc.AddApplicationPart(typeof(HostRatesController).Assembly);
        var services = mvc.Services;
        var original = services.Last(d => d.ServiceType == typeof(ICliPermissionCatalog));
        services.Remove(original);
        services.AddSingleton<ICliPermissionCatalog>(sp =>
            _catalog = new CountingPermissionCatalog((ICliPermissionCatalog)original.ImplementationFactory!(sp)));
    }

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync(o => o.ProtectServer = true, configureCli: Rates,
            configureApp: HostRoutes, configureMvc: CountCatalogReads);
        _root = await _h.RootTokenAsync();
        await _h.CreateGroupAsync(_root, "alice-group", "rates:read", "commands:execute");
        _aliceId = (await _h.CreateUserAsync(_root, "alice", groups: ["alice-group"])).GetProperty("id").GetString()!;
        _host = (await _h.Service<IApiTokenManager>().CreateAsync(_aliceId, new ApiTokenCreateOptions
        {
            Name = "site", Audience = ApiTokenAudience.Host, Permissions = ["rates:read"],
        })).Token;
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task HostToken_AcceptedByGuards()
    {
        Assert.Equal(200, (int)(await _h.GetAsync("/host/rates", _host)).StatusCode);
        Assert.Equal(200, (int)(await _h.GetAsync("/host/mvc-rates", _host)).StatusCode);
    }

    [Fact]
    public async Task HostToken_GuardChecksPermission()
    {
        Assert.Equal(200, (int)(await _h.PutAsync("groups/alice-group", _root,
            new { permissions = new[] { "commands:execute" } })).StatusCode);

        Assert.Equal("permission denied: rates:read", await UsersTestHost.AssertErrorAsync(await _h.GetAsync("/host/rates", _host), 403));
        Assert.Equal("permission denied: rates:read", await UsersTestHost.AssertErrorAsync(await _h.GetAsync("/host/mvc-rates", _host), 403));
    }

    [Theory]
    [InlineData("POST", "/api/v1/qcli/execute")]
    [InlineData("GET", "/api/v1/qcli/commands")]
    [InlineData("GET", "/api/v1/qcli/tokens")]
    [InlineData("GET", "/api/v1/qcli/permissions")]
    [InlineData("GET", "/api/v1/qcli/sessions/current")]
    [InlineData("POST", "/api/v1/qcli/sessions/ws-ticket")]
    [InlineData("GET", "/api/v1/qcli/users")]
    [InlineData("POST", "/api/qcli/mcp")]
    public async Task HostToken_RefusedOnCliRoutes(string method, string path)
    {
        var res = await _h.SendAsync(new HttpMethod(method), path, _host, method == "POST" ? new { } : null);

        Assert.Equal(403, (int)res.StatusCode);
        Assert.Equal(Refused, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HostToken_RefusedEvenWithNoPermissionsLeft()
    {
        Assert.Equal(200, (int)(await _h.PutAsync("groups/alice-group", _root,
            new { permissions = Array.Empty<string>() })).StatusCode);

        var res = await _h.GetAsync("/api/v1/qcli/commands", _host);

        Assert.Equal(403, (int)res.StatusCode);
        Assert.Equal(Refused, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CliToken_And_Session_Unchanged()
    {
        var session = (await _h.LoginAsync("alice", "pass1234")).Token;
        var cli = (await _h.Service<IApiTokenManager>().CreateAsync(_aliceId, new ApiTokenCreateOptions { Name = "cli" })).Token;

        foreach (var token in new[] { session, cli })
        {
            Assert.Equal(200, (int)(await _h.GetAsync("/api/v1/qcli/commands", token)).StatusCode);
            Assert.Equal(200, (int)(await _h.GetAsync("/host/rates", token)).StatusCode);
        }
    }

    [Fact]
    public async Task Bridge_RefusesHostToken()
    {
        var bridge = _h.Service<ICliAuthBridge>();
        var context = new DefaultHttpContext { RequestServices = _h.Host.Services };
        context.Request.Headers.Authorization = $"Bearer {_host}";

        var ex = await Assert.ThrowsAsync<CliAuthException>(() => bridge.AuthenticateAsync(context));
        Assert.Equal(403, ex.StatusCode);
        Assert.Equal("forbidden: host tokens cannot use CLI routes", ex.Message);

        var token = (await _h.Service<IApiTokenStore>().GetTokensAsync(_aliceId)).Single();
        var principal = new CliPrincipal(_aliceId, "alice", ["rates:read"], null, token.Id);
        Assert.Null(await bridge.RevalidateAsync(principal, touch: false));
    }

    [Fact]
    public async Task HostToken_GuardDoesNotConsultTheCatalog()
    {
        Interlocked.Exchange(ref _catalog.Reads, 0);

        Assert.Equal(200, (int)(await _h.GetAsync("/host/rates", _host)).StatusCode);
        Assert.Equal(200, (int)(await _h.GetAsync("/host/mvc-rates", _host)).StatusCode);

        Assert.Equal(0, _catalog.Reads);
    }

    [Fact]
    public async Task ExternalAuthWithApiTokens_HostTokenRefusedOnCliRoutes()
    {
        await using var h = await UsersTestHost.StartAsync(o =>
            {
                o.ProtectServer = true;
                o.UseExternalAuth(_ => null);
                o.AllowApiTokens();
            },
            configureCli: Rates, configureApp: HostRoutes);
        var store = h.Service<IUserStore>();
        await h.Service<IGroupStore>().CreateGroupAsync(new Group { Name = "rates-group", Permissions = ["rates:read", "commands:execute"] });
        var ann = await store.CreateUserAsync(new CreateUserDto { Name = "ann", Email = "ann@x.com", Groups = ["rates-group"] });
        var host = (await h.Service<IApiTokenManager>().CreateAsync(ann.Id, new ApiTokenCreateOptions
        {
            Name = "site", Audience = ApiTokenAudience.Host, Permissions = ["rates:read"],
        })).Token;

        var res = await h.GetAsync("/api/v1/qcli/commands", host);
        Assert.Equal(403, (int)res.StatusCode);
        Assert.Equal(Refused, await res.Content.ReadAsStringAsync());
        Assert.Equal(200, (int)(await h.GetAsync("/host/rates", host)).StatusCode);
    }

    [Theory]
    [InlineData("HOST")]
    [InlineData("api")]
    public async Task UnknownStoredAudience_IsRefusedOnCliRoutes(string audience)
    {
        await using var h = await UsersTestHost.StartAsync(o =>
            {
                o.ProtectServer = true;
                o.ApiTokenStore = new RespellingTokenStore(audience);
            },
            configureCli: Rates, configureApp: HostRoutes);
        var root = await h.RootTokenAsync();
        await h.CreateGroupAsync(root, "alice-group", "rates:read", "commands:execute");
        var aliceId = (await h.CreateUserAsync(root, "alice", groups: ["alice-group"])).GetProperty("id").GetString()!;
        var token = (await h.Service<IApiTokenManager>().CreateAsync(aliceId, new ApiTokenCreateOptions
        {
            Name = "site", Audience = ApiTokenAudience.Host, Permissions = ["rates:read"],
        })).Token;

        foreach (var path in new[] { "/api/v1/qcli/commands", "/api/v1/qcli/tokens" })
        {
            var res = await h.GetAsync(path, token);
            Assert.Equal(403, (int)res.StatusCode);
            Assert.Equal(Refused, await res.Content.ReadAsStringAsync());
        }
        Assert.Equal(200, (int)(await h.GetAsync("/host/rates", token)).StatusCode);
    }

    [Fact]
    public void EmptyOrNullAudience_ReadsAsCli()
    {
        Assert.Equal("cli", new ApiToken { Audience = null! }.Audience);
        Assert.Equal("cli", new ApiToken { Audience = "" }.Audience);
    }
}
