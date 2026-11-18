using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Users.Authorization;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>A host command that reports who <c>HttpContext.User</c> says is running it.</summary>
internal sealed class WhoAmIProcessor(IHttpContextAccessor http) : CliCommandProcessor
{
    public override string Command { get; set; } = "whoami-host";
    public override string Description { get; set; } = "Reports HttpContext.User";

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var user = http.HttpContext?.User;
        var id = user?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        return Task.FromResult($"user:{id}|type:{user?.Identity?.AuthenticationType}|method:{user?.FindFirstValue(CliHostUser.MethodClaim)}");
    }
}

/// <summary>
/// <see cref="Plugin.Users.UsersModuleOptions.SetHostUser"/>: a CLI request the users plugin authenticated also
/// carries the caller as <c>HttpContext.User</c>, so host code reading the signed-in user sees them.
/// </summary>
public class HostUserTests
{
    private static object Exec(string command) => new { command, value = "", args = new { }, chainCommands = Array.Empty<string>() };

    private static Task<UsersTestHost> StartAsync(bool setHostUser = true) =>
        UsersTestHost.StartAsync(
            o => o.SetHostUser = setHostUser,
            configureServices: s => s.AddHttpContextAccessor(),
            configureCli: cli => cli.AddProcessor<WhoAmIProcessor>(),
            // A host endpoint outside the CLI that reports its user.
            configureApp: app => app.Use(async (context, next) =>
            {
                if (context.Request.Path == "/host/me")
                {
                    await context.Response.WriteAsync(context.User.Identity?.IsAuthenticated == true ? context.User.Identity.Name! : "anonymous");
                    return;
                }
                await next();
            }));

    private static async Task<string> TokenAsync(UsersTestHost h, string root) =>
        (await UsersTestHost.ReadJsonAsync(await h.PostAsync("tokens", root, new { name = "terminal" }))).GetProperty("token").GetString()!;

    private static async Task<string> RunAsync(UsersTestHost h, string token, string command)
    {
        var res = await h.PostAsync("execute", token, Exec(command));
        Assert.Equal(200, (int)res.StatusCode);
        return JsonSerializer.Serialize(await UsersTestHost.ReadJsonAsync(res));
    }

    [Fact]
    public async Task AHostCommandRunWithAnApiTokenSeesTheTokensUser()
    {
        await using var h = await StartAsync();
        var root = await h.RootTokenAsync();
        var rootId = (await UsersTestHost.ReadJsonAsync(await h.GetAsync("sessions/current", root))).GetProperty("user").GetProperty("id").GetString();

        var output = await RunAsync(h, await TokenAsync(h, root), "whoami-host");

        Assert.Contains($"user:{rootId}", output);
        Assert.Contains($"type:{CliHostUser.AuthenticationType}", output);
        Assert.Contains("method:token", output);
    }

    [Fact]
    public async Task AHostCommandRunWithASessionSeesTheSessionsUser()
    {
        await using var h = await StartAsync();
        var root = await h.RootTokenAsync();

        Assert.Contains("method:session", await RunAsync(h, root, "whoami-host"));
    }

    [Fact]
    public async Task AnApiTokenDoesNotSignInToTheHostsOwnEndpoints()
    {
        await using var h = await StartAsync();
        var token = await TokenAsync(h, await h.RootTokenAsync());

        var req = new HttpRequestMessage(HttpMethod.Get, "/host/me");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        Assert.Equal("anonymous", await (await h.Client.SendAsync(req)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WithTheOptionOffTheHostUserStaysAnonymous()
    {
        await using var h = await StartAsync(setHostUser: false);
        var root = await h.RootTokenAsync();

        Assert.Contains("user:anonymous", await RunAsync(h, await TokenAsync(h, root), "whoami-host"));
    }

    [Fact]
    public async Task AUserTheHostAlreadySignedInIsKept()
    {
        await using var h = await UsersTestHost.StartAsync(
            configureServices: s => s.AddHttpContextAccessor(),
            configureCli: cli => cli.AddProcessor<WhoAmIProcessor>(),
            // The host's own authentication runs first and signs in "host-user".
            configureAuth: app => app.Use(async (context, next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "host-user")], "HostJwt"));
                await next();
            }));
        var root = await h.RootTokenAsync();

        var output = await RunAsync(h, await TokenAsync(h, root), "whoami-host");

        Assert.Contains("user:host-user", output);
        Assert.Contains("type:HostJwt", output);
    }
}
