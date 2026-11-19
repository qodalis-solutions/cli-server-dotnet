using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Users;
using Qodalis.Cli.Plugin.Users.Authorization;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>MVC host route that only <c>host</c> tokens may call.</summary>
[ApiController]
[Route("host/mvc-only")]
public class HostOnlyController : ControllerBase
{
    [HttpGet]
    [QcliAuthorize("rates:read", HostTokenOnly = true)]
    public IActionResult Get() => Ok(new { ok = true });
}

/// <summary>
/// C1-C3: a guarded host route sees its caller (<c>GetQcliCaller()</c>, <c>HttpContext.User</c>), <c>HostTokenOnly</c>
/// guards refuse sessions and <c>cli</c> tokens, and the guards leave metadata on the endpoint for OpenAPI.
/// </summary>
public class HostRouteCallerTests
{
    private const string HostTokenRequired = """{"error":"forbidden: a host token is required"}""";

    private sealed record Setup(UsersTestHost H, string Root, string AliceId, string HostToken, string HostTokenId,
        string CliToken, string Session) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => H.DisposeAsync();
    }

    private static void HostRoutes(IApplicationBuilder app) => app.UseEndpoints(e =>
    {
        e.MapGet("/host/caller", (HttpContext ctx) => Results.Json(new
        {
            caller = ctx.GetQcliCaller(),
            authenticated = ctx.User.Identity?.IsAuthenticated == true,
            name = ctx.User.Identity?.Name,
            claims = ctx.User.Claims.Select(c => new { type = c.Type, value = c.Value }),
        })).RequireQcliAuthorization("rates:read");
        e.MapGet("/host/only", () => "ok").RequireQcliAuthorization("rates:read", hostTokenOnly: true);
    });

    private static async Task<Setup> StartAsync(Action<UsersModuleOptions>? configure = null,
        Action<IApplicationBuilder>? configureAuth = null)
    {
        var h = await UsersTestHost.StartAsync(configure,
            configureCli: cli => cli.AddPermissions(p => p.Group("Rates").Add("rates:read", "Official exchange rates")),
            configureApp: HostRoutes,
            configureMvc: mvc => mvc.AddApplicationPart(typeof(HostOnlyController).Assembly),
            configureAuth: configureAuth);
        var root = await h.RootTokenAsync();
        await h.CreateGroupAsync(root, "alice-group", "rates:read", "commands:execute");
        var aliceId = (await h.CreateUserAsync(root, "alice", groups: ["alice-group"])).GetProperty("id").GetString()!;
        var created = await h.Service<IApiTokenManager>().CreateAsync(aliceId, new ApiTokenCreateOptions
        {
            Name = "site", Audience = ApiTokenAudience.Host, Permissions = ["rates:read"],
        });
        var session = (await h.LoginAsync("alice", "pass1234")).Token;
        var cliToken = (await UsersTestHost.ReadJsonAsync(await h.PostAsync("tokens", session, new { name = "terminal" })))
            .GetProperty("token").GetString()!;
        return new Setup(h, root, aliceId, created.Token, created.ApiToken.Id, cliToken, session);
    }

    private static async Task<JsonElement> CallerAsync(UsersTestHost h, string token)
    {
        var res = await h.GetAsync("/host/caller", token);
        Assert.Equal(200, (int)res.StatusCode);
        return await UsersTestHost.ReadJsonAsync(res);
    }

    private static List<(string Type, string Value)> Claims(JsonElement body) => body.GetProperty("claims").EnumerateArray()
        .Select(c => (c.GetProperty("type").GetString()!, c.GetProperty("value").GetString()!)).ToList();

    [Fact]
    public async Task Caller_HostToken()
    {
        await using var s = await StartAsync();

        var body = await CallerAsync(s.H, s.HostToken);

        var caller = body.GetProperty("caller");
        Assert.Equal(s.AliceId, caller.GetProperty("userId").GetString());
        Assert.Equal("alice", caller.GetProperty("userName").GetString());
        Assert.Equal(s.HostTokenId, caller.GetProperty("tokenId").GetString());
        Assert.Equal("host", caller.GetProperty("audience").GetString());
        Assert.Equal(["rates:read"], caller.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(["userId", "userName", "tokenId", "audience", "permissions"],
            caller.EnumerateObject().Select(p => p.Name));

        var claims = Claims(body);
        Assert.Contains((ClaimTypes.NameIdentifier, s.AliceId), claims);
        Assert.Contains((CliHostUser.MethodClaim, "token"), claims);
        Assert.Contains((CliHostUser.TokenClaim, s.HostTokenId), claims);
        Assert.Contains((CliHostUser.AudienceClaim, "host"), claims);
        Assert.Contains((CliHostUser.PermissionClaim, "rates:read"), claims);
        Assert.Equal("qcli:token", CliHostUser.TokenClaim);
        Assert.Equal("qcli:audience", CliHostUser.AudienceClaim);
    }

    [Fact]
    public async Task Caller_CliToken_HasCliAudience()
    {
        await using var s = await StartAsync();

        var body = await CallerAsync(s.H, s.CliToken);

        Assert.Equal("cli", body.GetProperty("caller").GetProperty("audience").GetString());
        Assert.Contains((CliHostUser.AudienceClaim, "cli"), Claims(body));
    }

    [Fact]
    public async Task Caller_Session()
    {
        await using var s = await StartAsync();

        var body = await CallerAsync(s.H, s.Session);

        var caller = body.GetProperty("caller");
        Assert.Equal(s.AliceId, caller.GetProperty("userId").GetString());
        Assert.Equal(JsonValueKind.Null, caller.GetProperty("tokenId").ValueKind);
        Assert.Equal(JsonValueKind.Null, caller.GetProperty("audience").ValueKind);
        var claims = Claims(body);
        Assert.Contains((CliHostUser.MethodClaim, "session"), claims);
        Assert.DoesNotContain(claims, c => c.Type == CliHostUser.TokenClaim || c.Type == CliHostUser.AudienceClaim);
    }

    [Fact]
    public async Task HostAuthenticatedUser_IsKept()
    {
        await using var s = await StartAsync(configureAuth: app => app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "host-user")], "HostJwt"));
            await next();
        }));

        var body = await CallerAsync(s.H, s.HostToken);

        Assert.Equal("host-user", body.GetProperty("name").GetString());
        Assert.Equal(s.AliceId, body.GetProperty("caller").GetProperty("userId").GetString());
    }

    [Fact]
    public async Task SetHostUserOff_LeavesUserAlone()
    {
        await using var s = await StartAsync(o => o.SetHostUser = false);

        var body = await CallerAsync(s.H, s.HostToken);

        Assert.False(body.GetProperty("authenticated").GetBoolean());
        Assert.Equal(s.AliceId, body.GetProperty("caller").GetProperty("userId").GetString());
    }

    [Theory]
    [InlineData("/host/only")]
    [InlineData("/host/mvc-only")]
    public async Task HostTokenOnly_RefusesSessionAndCliToken(string path)
    {
        await using var s = await StartAsync();

        foreach (var token in new[] { s.Session, s.CliToken })
        {
            var res = await s.H.GetAsync(path, token);
            Assert.Equal(403, (int)res.StatusCode);
            Assert.Equal(HostTokenRequired, await res.Content.ReadAsStringAsync());
        }
        Assert.Equal(200, (int)(await s.H.GetAsync(path, s.HostToken)).StatusCode);
    }

    [Fact]
    public async Task HostTokenOnly_AuthenticationFailuresKeepTheirTexts()
    {
        await using var s = await StartAsync();

        Assert.Equal("unauthorized", await UsersTestHost.AssertErrorAsync(await s.H.GetAsync("/host/only"), 401));
        Assert.Equal("invalid or expired token",
            await UsersTestHost.AssertErrorAsync(await s.H.GetAsync("/host/only", "qcli_pat_unknown"), 401));
    }

    [Fact]
    public async Task HostTokenOnly_ChecksPermissionAfterAudience()
    {
        await using var s = await StartAsync();
        var cliWithout = (await s.H.Service<IApiTokenManager>().CreateAsync(s.AliceId, new ApiTokenCreateOptions
        {
            Name = "narrow", Permissions = ["commands:execute"],
        })).Token;

        Assert.Equal("forbidden: a host token is required",
            await UsersTestHost.AssertErrorAsync(await s.H.GetAsync("/host/only", cliWithout), 403));

        Assert.Equal(200, (int)(await s.H.PutAsync("groups/alice-group", s.Root,
            new { permissions = new[] { "commands:execute" } })).StatusCode);
        Assert.Equal("permission denied: rates:read",
            await UsersTestHost.AssertErrorAsync(await s.H.GetAsync("/host/only", s.HostToken), 403));
        Assert.Equal("permission denied: rates:read",
            await UsersTestHost.AssertErrorAsync(await s.H.GetAsync("/host/mvc-only", s.HostToken), 403));
    }

    [Fact]
    public async Task Metadata_IsOnTheEndpoint()
    {
        await using var s = await StartAsync();
        var endpoints = s.H.Service<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        var only = endpoints.Single(e => e.RoutePattern.RawText == "/host/only");
        var metadata = Assert.Single(only.Metadata.GetOrderedMetadata<IQcliAuthorizeMetadata>());
        Assert.Equal("rates:read", metadata.Permission);
        Assert.True(metadata.HostTokenOnly);

        var caller = endpoints.Single(e => e.RoutePattern.RawText == "/host/caller");
        var plain = Assert.Single(caller.Metadata.GetOrderedMetadata<IQcliAuthorizeMetadata>());
        Assert.Equal("rates:read", plain.Permission);
        Assert.False(plain.HostTokenOnly);

        var mvc = endpoints.Single(e => e.RoutePattern.RawText == "host/mvc-only");
        // MVC lists the attribute both as an action attribute and as a filter: the same instance.
        var attribute = Assert.Single(mvc.Metadata.GetOrderedMetadata<IQcliAuthorizeMetadata>().Distinct());
        Assert.IsType<QcliAuthorizeAttribute>(attribute);
        Assert.Equal("rates:read", attribute.Permission);
        Assert.True(attribute.HostTokenOnly);
    }

    [Fact]
    public async Task RefusedGuards_LeaveNoCaller()
    {
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(string Path, int Status, bool Caller, bool User)>();
        var h = await UsersTestHost.StartAsync(
            configureCli: cli => cli.AddPermissions(p => p.Group("Rates").Add("rates:read", "Official exchange rates")),
            configureApp: app =>
            {
                app.Use(async (context, next) =>
                {
                    await next();
                    seen.Enqueue((context.Request.Path.Value!, context.Response.StatusCode, context.GetQcliCaller() != null,
                        context.User.Identity?.IsAuthenticated == true));
                });
                HostRoutes(app);
            },
            configureMvc: mvc => mvc.AddApplicationPart(typeof(HostOnlyController).Assembly));
        await using var _h = h;
        var root = await h.RootTokenAsync();
        await h.CreateGroupAsync(root, "bob-group", "commands:execute");
        await h.CreateUserAsync(root, "bob", groups: ["bob-group"]);
        var bob = (await h.LoginAsync("bob", "pass1234")).Token;

        Assert.Equal(403, (int)(await h.GetAsync("/host/only", bob)).StatusCode);      // audience
        Assert.Equal(403, (int)(await h.GetAsync("/host/mvc-only", bob)).StatusCode);  // audience (MVC)
        Assert.Equal(403, (int)(await h.GetAsync("/host/caller", bob)).StatusCode);    // permission

        Assert.All(seen.Where(s => s.Path.StartsWith("/host/")), s => Assert.False(s.Caller || s.User, s.ToString()));
    }

    [Fact]
    public async Task NoHostTokenOnly_Unchanged()
    {
        await using var s = await StartAsync();

        Assert.Equal(200, (int)(await s.H.GetAsync("/host/caller", s.CliToken)).StatusCode);
        Assert.Equal(200, (int)(await s.H.GetAsync("/host/caller", s.Session)).StatusCode);
    }

    [Fact]
    public async Task CliRoutes_StillRefuseHostTokens()
    {
        await using var s = await StartAsync();

        var res = await s.H.GetAsync("/api/v1/qcli/commands", s.HostToken);

        Assert.Equal(403, (int)res.StatusCode);
        Assert.Equal("""{"error":"forbidden: host tokens cannot use CLI routes"}""", await res.Content.ReadAsStringAsync());
    }
}
