using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.PublicApi;
using Qodalis.Cli.Plugin.Users.Authorization;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;

namespace Qodalis.Cli.Tests.Helpers;

/// <summary>
/// <see cref="UsersTestHost"/> with the public-api plugin and the guarded routes <c>GET /v1/rates</c>
/// (<c>rates:read</c>, answers the caller's token id, in the OpenAPI group <c>public</c>), <c>GET /v1/open</c> (no permission) and <c>GET /v1/missing</c>
/// (answers 404 itself).
/// </summary>
public static class PublicApiTestHost
{
    public static readonly DateTimeOffset LateEvening = new(2026, 11, 19, 23, 59, 30, TimeSpan.Zero);

    public static void Rates(CliBuilder cli) => cli.AddPermissions(p => p.Group("Rates")
        .Add("rates:read", "Official exchange rates")
        .Add("banks:read", "Banks and their buy and sell rates"));

    public static void MapRoutes(IEndpointRouteBuilder e)
    {
        e.MapGet("/v1/rates", (HttpContext ctx) => ctx.GetQcliCaller()!.TokenId).RequirePublicApi("rates:read")
            .WithGroupName("public").WithTags("Rates");
        e.MapGet("/v1/open", () => "open").RequirePublicApi();
        e.MapGet("/v1/missing", () => Results.NotFound()).RequirePublicApi();
    }

    public static Task<UsersTestHost> StartAsync(Action<PublicApiOptions>? configure = null,
        Action<IServiceCollection>? configureServices = null,
        Action<IApplicationBuilder>? configureApp = null,
        Action<IEndpointRouteBuilder>? endpoints = null,
        Action<IMvcBuilder>? configureMvc = null,
        Action<CliBuilder>? configureCli = null,
        bool validateScopes = false,
        Action<Qodalis.Cli.Plugin.Users.UsersModuleOptions>? configureUsers = null)
    {
        var time = new ManualTimeProvider();
        time.SetUtcNow(LateEvening);
        return UsersTestHost.StartAsync(
            configureUsers,
            configureServices: configureServices,
            configureCli: cli =>
            {
                Rates(cli);
                configureCli?.Invoke(cli);
                cli.AddPublicApi(configure);
            },
            configureApp: app =>
            {
                configureApp?.Invoke(app);
                app.UseEndpoints(e =>
                {
                    MapRoutes(e);
                    endpoints?.Invoke(e);
                });
            },
            configureMvc: configureMvc,
            time: time,
            validateScopes: validateScopes);
    }

    /// <summary>Creates <paramref name="userName"/> holding <paramref name="permissions"/> and a host token; returns (user id, token).</summary>
    public static async Task<(string UserId, string Token)> HostTokenAsync(this UsersTestHost h, string userName,
        params string[] permissions)
    {
        if (permissions.Length == 0) permissions = ["rates:read"];
        var root = await h.RootTokenAsync();
        await h.CreateGroupAsync(root, $"{userName}-group", permissions);
        var userId = (await h.CreateUserAsync(root, userName, groups: [$"{userName}-group"])).GetProperty("id").GetString()!;
        return (userId, await h.NewHostTokenAsync(userId, "site", permissions));
    }

    /// <summary>Another host token for <paramref name="userId"/>.</summary>
    public static async Task<string> NewHostTokenAsync(this UsersTestHost h, string userId, string name,
        params string[] permissions)
    {
        if (permissions.Length == 0) permissions = ["rates:read"];
        return (await h.Service<IApiTokenManager>().CreateAsync(userId, new ApiTokenCreateOptions
        {
            Name = name, Audience = ApiTokenAudience.Host, Permissions = permissions,
        })).Token;
    }
}
