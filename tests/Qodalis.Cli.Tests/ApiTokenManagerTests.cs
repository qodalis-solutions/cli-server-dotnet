using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// <see cref="IApiTokenManager"/>: tokens the host application creates, lists and revokes for its users,
/// including <c>host</c> tokens limited to the host's catalog entries.
/// </summary>
public class ApiTokenManagerTests : IAsyncLifetime
{
    private UsersTestHost _h = null!;
    private string _root = null!;
    private string _aliceId = null!;
    private IApiTokenManager _tokens = null!;

    private static void Rates(Qodalis.Cli.Extensions.CliBuilder cli) =>
        cli.AddPermissions(p => p.Group("Rates")
            .Add("rates:read", "Official exchange rates")
            .Add("banks:read", "Banks and their buy and sell rates"));

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync(configureCli: Rates);
        _root = await _h.RootTokenAsync();
        await _h.CreateGroupAsync(_root, "alice-group", "rates:read", "files:read");
        _aliceId = (await _h.CreateUserAsync(_root, "alice", groups: ["alice-group"])).GetProperty("id").GetString()!;
        _tokens = _h.Service<IApiTokenManager>();
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<CreatedApiToken> HostTokenAsync(string name = "site", params string[] permissions) =>
        _tokens.CreateAsync(_aliceId, new ApiTokenCreateOptions
        {
            Name = name, Audience = ApiTokenAudience.Host, Permissions = permissions.Length == 0 ? ["rates:read"] : permissions,
        });

    private static async Task<UsersApiException> ThrowsAsync(int status, string message, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<UsersApiException>(action);
        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(message, ex.Message);
        return ex;
    }

    // -------------------------------------------------------------------------
    // Create
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CreateHost_ReturnsTokenAndView()
    {
        using var events = new EventRecorder(_h.Service<ICliEventBus>());
        var now = _h.Time.GetUtcNow().ToUnixTimeMilliseconds();

        var created = await HostTokenAsync();

        Assert.StartsWith("qcli_pat_", created.Token);
        Assert.Equal("host", created.ApiToken.Audience);
        Assert.Equal(["rates:read"], created.ApiToken.Permissions);
        Assert.Equal("alice", created.ApiToken.UserName);
        Assert.Equal(now + 90L * 24 * 60 * 60 * 1000, created.ApiToken.ExpiresAt);
        var audit = await _h.Service<IAuditStore>().GetEntriesAsync();
        var entry = audit.Single(e => e.Event == AuditEvents.TokenCreated && e.Metadata?.TokenId == created.ApiToken.Id);
        Assert.Equal("alice", entry.Actor);
        var evt = await events.WaitAsync("token:created");
        Assert.Equal(created.ApiToken.Id, JsonSerializer.SerializeToElement(evt.Data).GetProperty("tokenId").GetString());

        // Same JSON names as the POST /tokens response
        var json = JsonSerializer.SerializeToElement(created);
        Assert.Equal(created.Token, json.GetProperty("token").GetString());
        Assert.Equal("host", json.GetProperty("apiToken").GetProperty("audience").GetString());
    }

    [Fact]
    public async Task CreateHost_WithActor_AuditsTheActor()
    {
        var created = await _tokens.CreateAsync(_aliceId, new ApiTokenCreateOptions
        {
            Name = "site", Audience = "host", Permissions = ["rates:read"], Actor = "rates-site",
        });

        var audit = await _h.Service<IAuditStore>().GetEntriesAsync();
        Assert.Equal("rates-site",
            audit.Single(e => e.Event == AuditEvents.TokenCreated && e.Metadata?.TokenId == created.ApiToken.Id).Actor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateHost_WithoutPermissions_Throws400(bool empty)
    {
        await ThrowsAsync(400, "host tokens need permissions", () => _tokens.CreateAsync(_aliceId, new ApiTokenCreateOptions
        {
            Name = "site", Audience = "host", Permissions = empty ? [] : null,
        }));
    }

    [Theory]
    [InlineData("files:read")]
    [InlineData("rates:*")]
    [InlineData("unknown:read")]
    public async Task CreateHost_NonHostEntry_Throws400(string permission)
    {
        await ThrowsAsync(400, $"not a host permission: {permission}", () => HostTokenAsync("site", permission));
    }

    [Fact]
    public async Task CreateHost_NotHeld_Throws403()
    {
        await ThrowsAsync(403, "forbidden: cannot grant banks:read", () => HostTokenAsync("site", "banks:read"));
    }

    [Fact]
    public async Task CreateCli_AcceptsFreeFormAndUnscoped()
    {
        var unscoped = await _tokens.CreateAsync(_aliceId, new ApiTokenCreateOptions { Name = "all" });
        Assert.Equal("cli", unscoped.ApiToken.Audience);
        Assert.Null(unscoped.ApiToken.Permissions);

        await ThrowsAsync(403, "forbidden: cannot grant files:*", () => _tokens.CreateAsync(_aliceId,
            new ApiTokenCreateOptions { Name = "files", Permissions = ["files:*"] }));
        var scoped = await _tokens.CreateAsync(_aliceId, new ApiTokenCreateOptions { Name = "files", Permissions = ["files:read"] });
        Assert.Equal(["files:read"], scoped.ApiToken.Permissions);
    }

    [Fact]
    public async Task Create_UnknownUser_Throws404()
    {
        await ThrowsAsync(404, "user not found", () => _tokens.CreateAsync("nobody", new ApiTokenCreateOptions { Name = "x" }));
    }

    [Fact]
    public async Task Create_DisabledUser_Throws403()
    {
        await _h.Service<IUserStore>().UpdateUserAsync(_aliceId, new UpdateUserDto { Disabled = true });

        await ThrowsAsync(403, "account disabled", () => _tokens.CreateAsync(_aliceId, new ApiTokenCreateOptions { Name = "x" }));
    }

    [Theory]
    [InlineData("api")]
    [InlineData("HOST")]
    [InlineData("")]
    public async Task Create_InvalidAudience_Throws400(string audience)
    {
        await ThrowsAsync(400, "audience must be cli or host", () => _tokens.CreateAsync(_aliceId,
            new ApiTokenCreateOptions { Name = "x", Audience = audience }));
    }

    [Fact]
    public async Task Create_InvalidName_Throws400()
    {
        await ThrowsAsync(400, "name must be 1-64 characters of [A-Za-z0-9._ -]", () => _tokens.CreateAsync(_aliceId,
            new ApiTokenCreateOptions { Name = "bad/name", Audience = "host", Permissions = ["files:read"] }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public async Task Create_ExpiryOutOfRange_Throws400(int days)
    {
        await ThrowsAsync(400, "expiresInDays must be an integer between 1 and 365", () => _tokens.CreateAsync(_aliceId,
            new ApiTokenCreateOptions { Name = "x", ExpiresInDays = days }));
    }

    [Fact]
    public async Task Create_ExpiresInDays_Applied()
    {
        var now = _h.Time.GetUtcNow().ToUnixTimeMilliseconds();

        var created = await _tokens.CreateAsync(_aliceId, new ApiTokenCreateOptions { Name = "x", ExpiresInDays = 7 });

        Assert.Equal(now + 7L * 24 * 60 * 60 * 1000, created.ApiToken.ExpiresAt);
    }

    [Fact]
    public async Task Create_BothExpiries_Throws400()
    {
        await ThrowsAsync(400, "expiresInDays and neverExpires cannot both be set", () => _tokens.CreateAsync(_aliceId,
            new ApiTokenCreateOptions { Name = "x", ExpiresInDays = 5, NeverExpires = true }));
    }

    [Fact]
    public async Task Create_NeverExpires_RequiresOption()
    {
        await ThrowsAsync(400, "non-expiring tokens are not allowed", () => _tokens.CreateAsync(_aliceId,
            new ApiTokenCreateOptions { Name = "x", NeverExpires = true }));
    }

    [Fact]
    public async Task Create_NeverExpires_WithOption()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.AllowNonExpiringTokens = true);
        var root = await h.Service<IUserStore>().GetUserAsync("root");

        var created = await h.Service<IApiTokenManager>().CreateAsync(root!.Id, new ApiTokenCreateOptions { Name = "x", NeverExpires = true });

        Assert.Null(created.ApiToken.ExpiresAt);
    }

    [Fact]
    public async Task Create_SharesNameAndLimitWithHttpTokens()
    {
        var session = (await _h.LoginAsync("alice", "pass1234")).Token;
        Assert.Equal(201, (int)(await _h.PostAsync("tokens", session, new { name = "site" })).StatusCode);

        await ThrowsAsync(409, "token 'SITE' already exists", () => HostTokenAsync("SITE"));

        for (var i = 1; i < 20; i++)
            await HostTokenAsync($"t{i}");
        await ThrowsAsync(400, "a user may have at most 20 tokens", () => HostTokenAsync("t20"));
        await UsersTestHost.AssertErrorAsync(await _h.PostAsync("tokens", session, new { name = "t21" }), 400);
    }

    [Fact]
    public async Task Create_ExternalAuthWithoutApiTokens_Throws501()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.UseExternalAuth(_ => null));
        var user = await h.Service<IUserStore>().CreateUserAsync(new CreateUserDto { Name = "ann", Email = "ann@x.com" });

        await ThrowsAsync(501, "Authentication is managed by the host application", () =>
            h.Service<IApiTokenManager>().CreateAsync(user.Id, new ApiTokenCreateOptions { Name = "x" }));
    }

    // -------------------------------------------------------------------------
    // List, revoke, grantable permissions
    // -------------------------------------------------------------------------

    [Fact]
    public async Task List_ReturnsBothAudiences_OldestFirst()
    {
        var session = (await _h.LoginAsync("alice", "pass1234")).Token;
        Assert.Equal(201, (int)(await _h.PostAsync("tokens", session, new { name = "first" })).StatusCode);
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await HostTokenAsync("second");
        await _tokens.CreateAsync(await RootIdAsync(), new ApiTokenCreateOptions { Name = "root token" });

        var list = await _tokens.ListAsync(_aliceId);

        Assert.Equal(["first", "second"], list.Select(t => t.Name));
        Assert.Equal(["cli", "host"], list.Select(t => t.Audience));
        Assert.All(list, t => Assert.Equal("alice", t.UserName));
    }

    private async Task<string> RootIdAsync() => (await _h.Service<IUserStore>().GetUserAsync("root"))!.Id;

    [Fact]
    public async Task Revoke_OwnToken_True_OtherUsersToken_False()
    {
        var created = await HostTokenAsync();
        var other = await HostTokenAsync("other");
        var store = _h.Service<IApiTokenStore>();

        Assert.False(await _tokens.RevokeAsync(await RootIdAsync(), created.ApiToken.Id));
        Assert.NotNull(await store.GetTokenAsync(created.ApiToken.Id));
        Assert.False(await _tokens.RevokeAsync(_aliceId, "missing"));

        Assert.True(await _tokens.RevokeAsync(_aliceId, created.ApiToken.Id));
        Assert.Null(await store.GetTokenAsync(created.ApiToken.Id));
        Assert.True(await _tokens.RevokeAsync(_aliceId, other.ApiToken.Id, actor: "rates-site"));

        var audit = await _h.Service<IAuditStore>().GetEntriesAsync(new AuditQueryOptions { Event = AuditEvents.TokenRevoked });
        Assert.Equal("alice", audit.Single(e => e.Metadata?.TokenId == created.ApiToken.Id).Actor);
        Assert.Equal("rates-site", audit.Single(e => e.Metadata?.TokenId == other.ApiToken.Id).Actor);
        await UsersTestHost.AssertErrorAsync(await _h.GetAsync("tokens", created.Token), 401);
    }

    [Fact]
    public async Task GrantablePermissions()
    {
        Assert.Equal(["rates:read"], (await _tokens.GetGrantablePermissionsAsync(_aliceId, "host")).Select(p => p.Name));
        Assert.Equal(["files:read", "rates:read"], (await _tokens.GetGrantablePermissionsAsync(_aliceId)).Select(p => p.Name));
        await ThrowsAsync(400, "audience must be cli or host", () => _tokens.GetGrantablePermissionsAsync(_aliceId, "api"));
        await ThrowsAsync(404, "user not found", () => _tokens.GetGrantablePermissionsAsync("nobody"));
    }

    [Fact]
    public async Task CreateHost_NormalizesCase()
    {
        var created = await HostTokenAsync("site", "RATES:READ");

        Assert.Equal(["rates:read"], created.ApiToken.Permissions);
        Assert.Equal(["rates:read"], (await _h.Service<IApiTokenStore>().GetTokenAsync(created.ApiToken.Id))!.Permissions);
    }

    [Fact]
    public async Task CreateHost_HeldThroughWildcard()
    {
        await _h.CreateGroupAsync(_root, "bob-group", "rates:*", "banks:*");
        var bobId = (await _h.CreateUserAsync(_root, "bob", groups: ["bob-group"])).GetProperty("id").GetString()!;

        var created = await _tokens.CreateAsync(bobId, new ApiTokenCreateOptions
        {
            Name = "site", Audience = "host", Permissions = ["rates:read", "banks:read"],
        });
        Assert.Equal(["rates:read", "banks:read"], created.ApiToken.Permissions);

        await ThrowsAsync(400, "not a host permission: rates:*", () => _tokens.CreateAsync(bobId, new ApiTokenCreateOptions
        {
            Name = "wild", Audience = "host", Permissions = ["rates:*"],
        }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task MissingUserId_Throws(string? userId)
    {
        var host = await HostTokenAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => _tokens.CreateAsync(userId!, new ApiTokenCreateOptions { Name = "x" }));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _tokens.ListAsync(userId!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _tokens.RevokeAsync(userId!, host.ApiToken.Id));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _tokens.GetGrantablePermissionsAsync(userId!));
        Assert.NotNull(await _h.Service<IApiTokenStore>().GetTokenAsync(host.ApiToken.Id));
    }
}
