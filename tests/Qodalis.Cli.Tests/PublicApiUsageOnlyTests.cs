using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.PublicApi;
using Qodalis.Cli.Plugin.PublicApi.EfCore;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Portal;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// The main application of a two-service host: it creates tokens and shows usage (<c>AddPublicApiUsage</c>) but serves
/// no public routes (no guard, no document, no portal section).
/// </summary>
public class PublicApiUsageOnlyTests
{
    private static Task<UsersTestHost> StartAsync(Action<Qodalis.Cli.Extensions.CliBuilder>? store = null) =>
        UsersTestHost.StartAsync(configureCli: cli =>
        {
            PublicApiTestHost.Rates(cli);
            cli.AddPublicApiUsage(o => o.UseQuotaResolver((_, _, _) => ValueTask.FromResult(new PublicApiQuota(7, null))));
            store?.Invoke(cli);
        });

    private static async Task<(string UserId, string TokenId)> TokenAsync(UsersTestHost h)
    {
        var root = await h.RootTokenAsync();
        await h.CreateGroupAsync(root, "alice-group", "rates:read");
        var aliceId = (await h.CreateUserAsync(root, "alice", groups: ["alice-group"])).GetProperty("id").GetString()!;
        var created = await h.Service<IApiTokenManager>().CreateAsync(aliceId, new ApiTokenCreateOptions
        {
            Name = "site", Audience = ApiTokenAudience.Host, Permissions = ["rates:read"],
        });
        return (aliceId, created.ApiToken.Id);
    }

    private static async Task AssertUsageAsync(UsersTestHost h)
    {
        var (aliceId, tokenId) = await TokenAsync(h);
        var day = PublicApiWindows.Day(h.Time.GetUtcNow());
        await h.Service<IPublicApiUsageStore>().IncrementAsync(tokenId, aliceId, day, CancellationToken.None);

        var view = Assert.Single(await h.Service<IApiTokenManager>().ListAsync(aliceId));
        Assert.Equal(1, view.Usage!.Today);
        Assert.Equal(30, view.Usage.Days.Count);

        var usage = await h.Service<IPublicApiUsage>().GetAccountUsageAsync(aliceId);
        Assert.Equal(1, usage.Today);
        Assert.Equal(new PublicApiQuota(7, null), usage.Quota);
        Assert.Equal(tokenId, Assert.Single(usage.Tokens).TokenId);
    }

    [Fact]
    public async Task UsageOnly_ListsUsageAndAccountUsage_WithoutPublicRoutes()
    {
        await using var h = await StartAsync();

        await AssertUsageAsync(h);

        Assert.Null(h.Host.Services.GetService(typeof(PublicApiGuard)));
        Assert.Null(h.Host.Services.GetService<PublicApiOptions>());
        Assert.Empty(h.Host.Services.GetServices<CliPortalSection>());
        Assert.Equal(404, (int)(await h.GetAsync("/qcli/api/openapi.json")).StatusCode);
        Assert.Equal(404, (int)(await h.GetAsync("/qcli/api/swagger/")).StatusCode);
    }

    [Fact]
    public async Task UsageOnly_WithTheEfCoreStore()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var h = await StartAsync(cli => cli.AddEfCorePublicApiUsageStore(o => o.UseSqlite(connection)));

        Assert.IsType<EfCorePublicApiUsageStore>(h.Service<IPublicApiUsageStore>());
        await AssertUsageAsync(h);
    }

    [Fact]
    public void AddPublicApiAndAddPublicApiUsage_Throw_InEitherOrder()
    {
        var first = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddControllers().AddCli(cli =>
            {
                cli.AddPublicApi();
                cli.AddPublicApiUsage();
            }));
        var second = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddControllers().AddCli(cli =>
            {
                cli.AddPublicApiUsage();
                cli.AddPublicApi();
            }));
        foreach (var ex in new[] { first, second })
            Assert.Contains("AddPublicApi or AddPublicApiUsage, not both", ex.Message);

        // AddPublicApiUsage twice is fine (the last options win).
        new ServiceCollection().AddControllers().AddCli(cli => { cli.AddPublicApiUsage(); cli.AddPublicApiUsage(); });
    }
}
