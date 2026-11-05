using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.Audit;
using Qodalis.Cli.Plugin.Audit.Controllers;
using Qodalis.Cli.Plugin.Audit.Extensions;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Tests;

public class AuditPluginTests
{
    private sealed class CustomAuditStore : InMemoryAuditStore;

    private static ServiceProvider Build(Action<CliBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddCli(configure);
        return services.BuildServiceProvider();
    }

    private static IReadOnlyList<string> InstalledIds(IServiceProvider sp) =>
        [.. sp.GetRequiredService<CliInstalledPlugins>().All.Select(p => p.Id)];

    [Fact]
    public void AddAudit_KeepsTheLogInMemory_AndIsListedOnce()
    {
        using var sp = Build(cli => cli.AddAudit().AddAudit());

        Assert.IsType<InMemoryAuditStore>(sp.GetRequiredService<IAuditStore>());
        Assert.Single(InstalledIds(sp), id => id == "Qodalis.Cli.Plugin.Audit");
    }

    [Fact]
    public void TheUsersPlugin_AddsTheAuditPlugin_AndKeepsAStoreSetBeforeOrAfter()
    {
        using var users = Build(cli => cli.AddUsers());
        using var before = Build(cli => cli.SetAuditStore<CustomAuditStore>().AddUsers());
        using var after = Build(cli => cli.AddUsers().SetAuditStore<CustomAuditStore>());
        using var option = Build(cli => cli.AddUsers(o => o.SetAuditStore<CustomAuditStore>()));

        Assert.Contains("Qodalis.Cli.Plugin.Audit", InstalledIds(users));
        Assert.IsType<InMemoryAuditStore>(users.GetRequiredService<IAuditStore>());
        Assert.IsType<CustomAuditStore>(before.GetRequiredService<IAuditStore>());
        Assert.IsType<CustomAuditStore>(after.GetRequiredService<IAuditStore>());
        Assert.IsType<CustomAuditStore>(option.GetRequiredService<IAuditStore>());
    }

    [Fact]
    public async Task GetEntries_ReturnsTheDefaultPage_NeverMoreThanTheMaximum_AndRefusesNegativePaging()
    {
        var store = new InMemoryAuditStore(5000);
        for (var i = 0; i < CliAuditController.MaxLimit + 5; i++)
            await store.LogAsync(new AuditEntry { Event = "note", Username = $"u{i}", Timestamp = i });
        var controller = new CliAuditController(store);

        var page = Assert.IsType<OkObjectResult>(await controller.GetEntries(null, null, null, null, null));
        var most = Assert.IsType<OkObjectResult>(await controller.GetEntries(null, null, null, 5000, null));
        var filtered = Assert.IsType<OkObjectResult>(await controller.GetEntries("NOTE", "U3", null, null, null));

        Assert.Equal(CliAuditController.DefaultLimit, Assert.IsType<List<AuditEntry>>(page.Value).Count);
        Assert.Equal(CliAuditController.MaxLimit, Assert.IsType<List<AuditEntry>>(most.Value).Count);
        Assert.Equal("u3", Assert.Single(Assert.IsType<List<AuditEntry>>(filtered.Value)).Username);
        Assert.IsType<BadRequestObjectResult>(await controller.GetEntries(null, null, null, -1, null));
        Assert.IsType<BadRequestObjectResult>(await controller.GetEntries(null, null, null, null, -1));
    }

    [Fact]
    public async Task Clear_EmptiesTheLog_AndRecordsWhoClearedIt()
    {
        var store = new InMemoryAuditStore();
        await store.LogAsync(new AuditEntry { Event = "login", Username = "bob", Timestamp = 1 });
        var http = new DefaultHttpContext();
        http.SetCliPrincipal(new CliPrincipal("u1", "alice", ["audit:clear"]));
        var controller = new CliAuditController(store) { ControllerContext = new ControllerContext { HttpContext = http } };

        Assert.IsType<NoContentResult>(await controller.Clear());

        var entry = Assert.Single(await store.GetEntriesAsync());
        Assert.Equal(CliAuditController.ClearedEvent, entry.Event);
        Assert.Equal("alice", entry.Actor);
        Assert.Equal("u1", entry.ActorId);
    }
}
