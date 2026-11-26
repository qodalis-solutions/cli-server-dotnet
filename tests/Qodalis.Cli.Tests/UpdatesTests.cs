using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Abstractions.Notifications;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Notifications;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Plugin.Jobs;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>A marketplace's annotated list in memory: what it answers, failures to come, a gate holding the calls.</summary>
internal sealed class FakeUpdateSearch
{
    public List<JsonObject> Items { get; set; } = [];
    public ConcurrentQueue<Exception> Failures { get; } = new();
    public ConcurrentQueue<List<JsonObject>> Answers { get; } = new();
    public ConcurrentQueue<(int Page, int PageSize)> Calls { get; } = new();
    public SemaphoreSlim Called { get; } = new(0);
    public TaskCompletionSource? Gate { get; set; }

    public async Task<JsonObject> SearchAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        Calls.Enqueue((page, pageSize));
        Called.Release();
        if (Gate != null) await Gate.Task;
        if (Failures.TryDequeue(out var failure)) throw failure;
        var items = Answers.TryDequeue(out var answer) ? answer : Items;
        return new JsonObject
        {
            ["items"] = new JsonArray(items.Select(i => (JsonNode?)i.DeepClone()).ToArray()),
            ["total"] = items.Count, ["page"] = page, ["pageSize"] = pageSize,
        };
    }

    public async Task WaitForCallAsync() => Assert.True(await Called.WaitAsync(TimeSpan.FromSeconds(10)), "the marketplace was not asked");
}

/// <summary>The NuGet flat container's <c>qodalis.cli/index.json</c> in memory.</summary>
internal sealed class FakeNuGetFeed : HttpMessageHandler
{
    public const string Feed = "https://nuget.test/v3-flatcontainer";
    public string[]? Versions { get; set; } = ["1.0.0-beta.33"];
    public bool Down { get; set; }
    public ConcurrentQueue<string> Calls { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Calls.Enqueue(url);
        if (Down) throw new HttpRequestException("connection refused");
        if (url != $"{Feed}/qodalis.cli/index.json" || Versions == null)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        var body = new JsonObject { ["versions"] = new JsonArray(Versions.Select(v => (JsonNode?)v).ToArray()) };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });
    }
}

/// <summary>The jobs plugin's registry as the update checker sees it: what was registered.</summary>
internal sealed class FakeJobRegistry : ICliJobRegistry
{
    public List<(ICliJob Job, CliJobOptions Options)> Registered { get; } = [];

    public bool IsRegistered(string id) => Registered.Any(r => r.Options.Id == id);

    public string Register(ICliJob job, CliJobOptions options)
    {
        Registered.Add((job, options));
        return options.Id!;
    }
}

internal sealed class FakeJobContext : ICliJobExecutionContext
{
    public List<string> Lines { get; } = [];
    public ICliJobLogger Logger => new LineLogger(Lines);
    public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();
    public IDictionary<string, object?> Properties { get; } = new Dictionary<string, object?>();
    public void Discard() { }

    private sealed class LineLogger(List<string> lines) : ICliJobLogger
    {
        public void Debug(string message) => lines.Add(message);
        public void Info(string message) => lines.Add(message);
        public void Warning(string message) => lines.Add(message);
        public void Error(string message) => lines.Add(message);
    }
}

/// <summary>
/// The update checker: dashboard and server plugins installed from the marketplace with a newer version (Update),
/// plugins added in code (Update in code, with the install commands per platform), the server package (NuGet), the
/// notifications it publishes and resolves, failures keeping the last items, joining a running check, its persisted
/// result, its schedule (a job of the jobs plugin, else a timer), <c>/api/v1/qcli/updates</c> with its permissions, the
/// <c>updates_list</c> MCP tool, and the jobs scheduler scheduling a job registered after it started.
/// </summary>
public sealed class UpdatesTests : IAsyncLifetime
{
    private const string Url = "/api/v1/qcli/updates";
    private const string Tickets = "@acme/cli-dashboard-tickets";
    private const string JobsModule = "@qodalis/cli-dashboard-module-jobs";

    private readonly MarketplaceTestServers _servers = new();
    private readonly List<IAsyncDisposable> _hosts = [];
    private readonly List<IDisposable> _disposables = [];
    private readonly List<string> _dirs = [];
    private static readonly CliNotificationViewer Everyone = CliNotificationViewer.Anonymous;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables) d.Dispose();
        foreach (var host in _hosts) await host.DisposeAsync();
        await _servers.DisposeAsync();
        foreach (var dir in _dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qcli-updates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private UpdateChecker Checker(FakeUpdateSearch? server = null, FakeUpdateSearch? dashboard = null, FakeNuGetFeed? nuget = null,
        CliNotificationService? notifications = null, string? file = null, string? version = "1.0.0-beta.33", AdminUpdateCheckOptions? options = null,
        TimeProvider? time = null, ICliJobRegistry? jobs = null, List<(string, string)>? serverInstalled = null, List<(string, string)>? dashboardInstalled = null)
    {
        var checker = new UpdateChecker(new UpdateCheckerSources
        {
            ServerSearch = server == null ? null : server.SearchAsync,
            ServerInstalled = () => serverInstalled ?? [],
            DashboardSearch = dashboard == null ? null : dashboard.SearchAsync,
            DashboardInstalled = () => dashboardInstalled ?? [],
            ServerVersion = () => version,
            NuGetFeed = FakeNuGetFeed.Feed,
            HttpHandler = nuget ?? new FakeNuGetFeed(),
            Notifications = notifications == null ? null : () => notifications,
            Jobs = jobs == null ? null : () => jobs,
            File = file,
            Time = time,
        }, options);
        _disposables.Add(checker);
        return checker;
    }

    private static JsonObject ServerItem(string name, (string Version, string Source)? installed, string latest = "1.2.0", string? package = null,
        bool allowed = true, string? blockedReason = null, string? title = null, JsonObject? platforms = null)
    {
        package ??= $"Qodalis.Cli.Plugin.{char.ToUpperInvariant(name[0])}{name[1..]}";
        var item = new JsonObject
        {
            ["name"] = name,
            ["title"] = title ?? $"{char.ToUpperInvariant(name[0])}{name[1..]}",
            ["icon"] = "🌤️",
            ["platforms"] = platforms ?? new JsonObject
            {
                ["dotnet"] = new JsonObject { ["package"] = package, ["latest"] = latest },
                ["node"] = new JsonObject { ["package"] = $"@qodalis/cli-server-plugin-{name}", ["latest"] = latest },
                ["python"] = new JsonObject { ["package"] = $"qodalis-cli-server-plugin-{name}", ["latest"] = "1.2.0b1", ["install"] = $"pip install qodalis-cli-server-plugin-{name}==1.2.0b1" },
            },
            ["platform"] = new JsonObject { ["package"] = package, ["latest"] = latest, ["installed"] = installed?.Version, ["compatible"] = true },
            ["installed"] = installed == null ? null : new JsonObject { ["version"] = installed.Value.Version, ["source"] = installed.Value.Source },
            ["updateAvailable"] = installed != null,
            ["allowed"] = allowed,
        };
        if (blockedReason != null) item["blockedReason"] = blockedReason;
        return item;
    }

    private static JsonObject DashboardItem(string name, (string Version, string Source)? installed, string latest, bool allowed = true, string? title = null) => new()
    {
        ["name"] = name, ["title"] = title ?? name, ["kind"] = "dashboard", ["version"] = latest, ["latest"] = latest,
        ["installed"] = installed == null ? null : new JsonObject { ["version"] = installed.Value.Version, ["source"] = installed.Value.Source },
        ["updateAvailable"] = installed != null, ["allowed"] = allowed,
    };

    private static string J(object? value) => JsonSerializer.Serialize(value);

    // ---------------------------------------------------------------- the checker

    [Fact]
    public async Task ServerPlugins_MarketplaceInstallsUpdateHere_CodeAddedOnesInCode_WithTheInstallCommandPerPlatform()
    {
        var server = new FakeUpdateSearch
        {
            Items =
            [
                ServerItem("weather", ("1.0.0", "marketplace")),
                ServerItem("jobs", ("0.9.0", "code"), title: "Jobs", platforms: new JsonObject
                {
                    ["dotnet"] = new JsonObject { ["package"] = "Qodalis.Cli.Plugin.Jobs", ["latest"] = "1.2.0" },
                    ["node"] = new JsonObject { ["package"] = "@qodalis/cli-server-plugin-jobs", ["latest"] = "1.2.0" },
                    ["python"] = new JsonObject { ["package"] = "qodalis-cli-server-plugin-jobs", ["latest"] = "1.2.0b1" },
                }),
                ServerItem("other", null),
                ServerItem("shady", ("1.0.0", "marketplace"), latest: "1.1.0", allowed: false, blockedReason: "unverified publisher"),
            ],
        };
        var notifications = new CliNotificationService();
        var c = Checker(server, notifications: notifications);
        var result = await c.CheckAsync();

        Assert.Equal((1, 100), server.Calls.Single());
        Assert.Equal(J(new
        {
            kind = "server", name = "weather", package = "Qodalis.Cli.Plugin.Weather", title = "Weather", icon = "🌤️", current = "1.0.0", latest = "1.2.0",
            source = "marketplace", updateAvailable = true, canUpdate = true,
        }), J(result.ServerPlugins[0]));
        var shady = result.ServerPlugins[1];
        Assert.Equal(("shady", false, "unverified publisher", "1.1.0"), (shady.Name, shady.CanUpdate, shady.Reason, shady.Latest));
        var jobs = Assert.Single(result.CodePlugins);
        Assert.Equal(("server", "jobs", "Qodalis.Cli.Plugin.Jobs", "code", "0.9.0", "1.2.0", false, "update in code"),
            (jobs.Kind, jobs.Name, jobs.Package, jobs.Source, jobs.Current, jobs.Latest, jobs.CanUpdate, jobs.Reason));
        // this platform's command first, then the others' (the marketplace's own command when it gives one)
        Assert.Equal(["dotnet", "node", "python"], jobs.Install!.Keys);
        Assert.Equal(J(new Dictionary<string, string>
        {
            ["dotnet"] = "dotnet add package Qodalis.Cli.Plugin.Jobs --version 1.2.0",
            ["node"] = "npm install @qodalis/cli-server-plugin-jobs@1.2.0",
            ["python"] = "pip install --upgrade qodalis-cli-server-plugin-jobs==1.2.0b1",
        }), J(jobs.Install));
        Assert.Equal(3, result.Count);
        Assert.Equal(("core", "Qodalis.Cli", "1.0.0-beta.33", "1.0.0-beta.33", false, "nuget"),
            (result.Server!.Kind, result.Server.Package, result.Server.Current, result.Server.Latest, result.Server.UpdateAvailable, result.Server.Source));
        Assert.NotNull(result.CheckedAt);
        Assert.Null(result.Error);
        Assert.False(result.Checking);

        Assert.Equal(["update:code:server:Qodalis.Cli.Plugin.Jobs", "update:server:shady", "update:server:weather"],
            notifications.List(Everyone).Items.Select(n => n.Key).Order());
        var weather = notifications.Get("update:server:weather")!;
        Assert.Equal(("update", "info", "Update available: Weather 1.2.0", "/plugins?tab=updates", "updates:read", "Qodalis.Cli.Plugin.Weather 1.0.0 → 1.2.0."),
            (weather.Kind, weather.Severity, weather.Title, weather.Link, weather.RequiredPermission, weather.Message));
        Assert.Equal(J(new { kind = "server", name = "weather", package = "Qodalis.Cli.Plugin.Weather", current = "1.0.0", latest = "1.2.0", source = "marketplace", canUpdate = true }),
            weather.Data!.ToJsonString());
        Assert.Equal("Qodalis.Cli.Plugin.Shady 1.0.0 → 1.1.0. unverified publisher", notifications.Get("update:server:shady")!.Message);
        var code = notifications.Get("update:code:server:Qodalis.Cli.Plugin.Jobs")!;
        Assert.Equal("Update in code: Jobs 1.2.0", code.Title);
        Assert.Equal("Qodalis.Cli.Plugin.Jobs 0.9.0 → 1.2.0. The host adds it in code: dotnet add package Qodalis.Cli.Plugin.Jobs --version 1.2.0, then restart.", code.Message);
    }

    [Fact]
    public async Task DashboardPlugins_MarketplaceInstalls_AndPagesAddedInCode_WithTheNpmCommand()
    {
        var dashboard = new FakeUpdateSearch
        {
            Items =
            [
                DashboardItem(Tickets, ("1.0.0", "marketplace"), "1.1.0", title: "Tickets"),
                DashboardItem(JobsModule, ("1.0.0", "code"), "1.3.0", title: "Jobs"),
                DashboardItem("@acme/not-installed", null, "2.0.0"),
            ],
        };
        var notifications = new CliNotificationService();
        var result = await Checker(dashboard: dashboard, notifications: notifications).CheckAsync();
        var tickets = Assert.Single(result.DashboardPlugins);
        Assert.Equal(("dashboard", Tickets, Tickets, "Tickets", "1.0.0", "1.1.0", "marketplace", true), (tickets.Kind, tickets.Name, tickets.Package, tickets.Title,
            tickets.Current, tickets.Latest, tickets.Source, tickets.CanUpdate));
        Assert.Null(tickets.Install);
        var jobs = Assert.Single(result.CodePlugins);
        Assert.Equal(J(new Dictionary<string, string> { ["node"] = $"npm install {JobsModule}@1.3.0" }), J(jobs.Install));
        Assert.Equal(2, result.Count);
        Assert.Equal([$"update:code:dashboard:{JobsModule}", $"update:dashboard:{Tickets}"], notifications.List(Everyone).Items.Select(n => n.Key).Order());
    }

    [Fact]
    public async Task TheServerPackage_NewerOnNuGet_IsAnUpdateAndANotification_PrereleasesCompareAsSemver()
    {
        var nuget = new FakeNuGetFeed { Versions = ["1.0.0-beta.9", "1.0.0-beta.40", "1.0.0-beta.33", "0.9.0"] };
        var notifications = new CliNotificationService();
        var result = await Checker(nuget: nuget, notifications: notifications).CheckAsync();
        Assert.Equal(J(new
        {
            kind = "core", name = "Qodalis.Cli", package = "Qodalis.Cli", title = "Server", icon = (string?)null, current = "1.0.0-beta.33",
            latest = "1.0.0-beta.40", source = "nuget", updateAvailable = true, canUpdate = false,
            reason = "update the server packages in code (every Qodalis.Cli* package shares the version)",
            install = new Dictionary<string, string> { ["dotnet"] = "dotnet add package Qodalis.Cli --version 1.0.0-beta.40" },
        }), J(result.Server));
        Assert.Equal(1, result.Count);
        Assert.Equal($"{FakeNuGetFeed.Feed}/qodalis.cli/index.json", nuget.Calls.Single());
        var n = notifications.Get("update:core:Qodalis.Cli")!;
        Assert.Equal(("Server update available: 1.0.0-beta.40", "Qodalis.Cli 1.0.0-beta.33 → 1.0.0-beta.40. Update the server packages in code and restart."),
            (n.Title, n.Message));

        // the server updated: the notification is resolved
        nuget.Versions = ["1.0.0-beta.33"];
        Assert.False((await Checker(nuget: nuget, notifications: notifications).CheckAsync()).Server!.UpdateAvailable);
        Assert.Null(notifications.Get("update:core:Qodalis.Cli"));

        // a package NuGet does not have: no newest version, no error
        nuget.Versions = null;
        var unknown = await Checker(nuget: nuget).CheckAsync();
        Assert.Equal((null, false, null), (unknown.Server!.Latest, unknown.Server.UpdateAvailable, unknown.Error));
    }

    [Fact]
    public async Task ResolvesTheNotificationsOfUpdatesThatNoLongerApply()
    {
        var server = new FakeUpdateSearch { Items = [ServerItem("weather", ("1.0.0", "marketplace"))] };
        var notifications = new CliNotificationService();
        var c = Checker(server, notifications: notifications);
        await c.CheckAsync();
        Assert.NotNull(notifications.Get("update:server:weather"));
        var current = ServerItem("weather", ("1.2.0", "marketplace"));
        current["updateAvailable"] = false;
        server.Answers.Enqueue([current]);
        var result = await c.CheckAsync();
        Assert.Empty(result.ServerPlugins);
        Assert.Null(notifications.Get("update:server:weather"));
    }

    [Fact]
    public async Task AMarketplaceThatDoesNotAnswer_KeepsTheLastItemsAndTheirNotifications_AndSaysWhy()
    {
        var server = new FakeUpdateSearch { Items = [ServerItem("weather", ("1.0.0", "marketplace")), ServerItem("jobs", ("0.9.0", "code"))] };
        var dashboard = new FakeUpdateSearch { Items = [DashboardItem(Tickets, ("1.0.0", "marketplace"), "1.1.0")] };
        var notifications = new CliNotificationService();
        var c = Checker(server, dashboard, notifications: notifications);
        await c.CheckAsync();
        server.Failures.Enqueue(new DashboardMarketplaceException(502, "marketplace unreachable"));
        dashboard.Failures.Enqueue(new DashboardMarketplaceException(502, "marketplace unreachable"));
        var result = await c.CheckAsync();
        Assert.Equal("dashboard plugins: marketplace unreachable; server plugins: marketplace unreachable", result.Error);
        Assert.Equal(["weather"], result.ServerPlugins.Select(i => i.Name));
        Assert.Equal([Tickets], result.DashboardPlugins.Select(i => i.Name));
        Assert.Equal(["jobs"], result.CodePlugins.Select(i => i.Name));
        Assert.Equal(3, result.Count);
        Assert.NotNull(notifications.Get("update:server:weather"));
        Assert.NotNull(notifications.Get($"update:dashboard:{Tickets}"));
        Assert.NotNull(notifications.Get("update:code:server:Qodalis.Cli.Plugin.Jobs"));

        // only one category down: the other one's answer applies
        server.Answers.Enqueue([]);
        dashboard.Failures.Enqueue(new HttpRequestException("boom"));
        var partly = await c.CheckAsync();
        Assert.Equal("dashboard plugins: boom", partly.Error);
        Assert.Empty(partly.ServerPlugins);
        Assert.Empty(partly.CodePlugins);
        Assert.Equal([Tickets], partly.DashboardPlugins.Select(i => i.Name));
        Assert.Null(notifications.Get("update:server:weather"));
        Assert.NotNull(notifications.Get($"update:dashboard:{Tickets}"));
    }

    [Fact]
    public async Task NuGetDown_SaysSo_AndKeepsTheLastServerItem()
    {
        var nuget = new FakeNuGetFeed { Versions = ["1.0.0-beta.40"] };
        var c = Checker(nuget: nuget);
        await c.CheckAsync();
        nuget.Down = true;
        var result = await c.CheckAsync();
        Assert.Equal("server package: NuGet feed unreachable", result.Error);
        Assert.Equal(("1.0.0-beta.40", true, 1), (result.Server!.Latest, result.Server.UpdateAvailable, result.Count));
    }

    [Fact]
    public async Task JoinsACheckAlreadyRunning()
    {
        var server = new FakeUpdateSearch { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var c = Checker(server);
        var first = c.CheckAsync();
        await server.WaitForCallAsync();
        Assert.True(c.Result().Checking);
        var second = c.CheckAsync();
        server.Gate.SetResult();
        var (a, b) = (await first, await second);
        Assert.Single(server.Calls);
        Assert.Equal(J(a), J(b));
        Assert.False(c.Result().Checking);
    }

    [Fact]
    public async Task KeepsTheLastResultAcrossARestart_TheRunningVersionReadAgain()
    {
        var file = Path.Combine(TempDir(), "data", UpdateChecker.FileName);
        var server = new FakeUpdateSearch { Items = [ServerItem("weather", ("1.0.0", "marketplace"))] };
        await Checker(server, nuget: new FakeNuGetFeed { Versions = ["1.0.0-beta.40"] }, file: file).CheckAsync();
        var saved = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.False(saved.ContainsKey("checking"));
        Assert.Equal(2, saved["count"]!.GetValue<int>());

        var again = Checker(server, file: file).Result();
        Assert.Equal((2, false, "weather", "1.0.0-beta.40", true), (again.Count, again.Checking, again.ServerPlugins.Single().Name, again.Server!.Latest,
            again.Server.UpdateAvailable));
        // the server was updated in code since: its running version is read again
        var updated = Checker(server, file: file, version: "1.0.0-beta.40").Result();
        Assert.Equal((1, "1.0.0-beta.40", false), (updated.Count, updated.Server!.Current, updated.Server.UpdateAvailable));

        // before the first check
        var empty = Checker(server, file: Path.Combine(TempDir(), "none.json")).Result();
        Assert.Equal("{\"checkedAt\":null,\"checking\":false,\"server\":null,\"dashboardPlugins\":[],\"serverPlugins\":[],\"codePlugins\":[],\"count\":0}", J(empty));
        // a damaged file is ignored
        var bad = Path.Combine(TempDir(), "bad.json");
        File.WriteAllText(bad, "{ nope");
        Assert.Null(Checker(server, file: bad).Result().CheckedAt);
    }

    [Fact]
    public async Task RefreshInstalled_DropsUpdatedOrRemovedMarketplacePlugins_WithoutAskingTheMarketplace()
    {
        var installed = new List<(string, string)> { ("weather", "1.0.0"), ("radar", "2.0.0") };
        var server = new FakeUpdateSearch { Items = [ServerItem("weather", ("1.0.0", "marketplace")), ServerItem("radar", ("2.0.0", "marketplace"), latest: "2.1.0")] };
        var notifications = new CliNotificationService();
        var c = Checker(server, notifications: notifications, serverInstalled: installed);
        await c.CheckAsync();
        c.RefreshInstalled();
        Assert.Equal(2, c.Result().ServerPlugins.Count);
        installed[0] = ("weather", "1.2.0");
        installed.RemoveAt(1);
        c.RefreshInstalled();
        Assert.Equal((0, 0), (c.Result().ServerPlugins.Count, c.Result().Count));
        Assert.Null(notifications.Get("update:server:weather"));
        Assert.Null(notifications.Get("update:server:radar"));
        Assert.Single(server.Calls);
    }

    [Fact]
    public async Task Schedule_AJobOfTheJobsPlugin_TheFirstCheckAfterTheInitialDelay()
    {
        var clock = new JobTestClock(DateTimeOffset.Parse("2026-11-26T10:00:00Z"));
        var jobs = new FakeJobRegistry();
        var server = new FakeUpdateSearch();
        var c = Checker(server, time: clock, jobs: jobs);
        c.Start();
        c.Start();
        var (job, options) = Assert.Single(jobs.Registered);
        Assert.Equal(("updates:check", "updates:check", "system", TimeSpan.FromHours(6)), (options.Id, options.Name, options.Group, options.Interval));
        Assert.Equal(["system", "updates"], options.Tags);
        Assert.Empty(server.Calls);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(c.CurrentCheck);
        clock.Advance(TimeSpan.FromSeconds(1));
        await server.WaitForCallAsync();
        await (c.CurrentCheck ?? Task.CompletedTask);

        // the job runs the check; a failed check fails the job
        var context = new FakeJobContext();
        await job.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(2, server.Calls.Count);
        Assert.Equal(["0 update(s) available"], context.Lines);
        server.Failures.Enqueue(new DashboardMarketplaceException(502, "marketplace unreachable"));
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => job.ExecuteAsync(new FakeJobContext(), CancellationToken.None));
        Assert.Equal("server plugins: marketplace unreachable", failed.Message);
        // no timer of its own besides the first check's (fired)
        Assert.Equal(0, clock.ArmedTimers);
        c.Stop();
    }

    [Fact]
    public async Task Schedule_ATimerWithoutTheJobsPlugin_AFreshLastResultWaits_DisabledNothing()
    {
        var clock = new JobTestClock(DateTimeOffset.Parse("2026-11-26T10:00:00Z"));
        var server = new FakeUpdateSearch();
        var c = Checker(server, time: clock);
        c.Start();
        clock.Advance(TimeSpan.FromSeconds(60));
        await server.WaitForCallAsync();
        await (c.CurrentCheck ?? Task.CompletedTask);
        clock.Advance(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(60));
        await server.WaitForCallAsync();
        await (c.CurrentCheck ?? Task.CompletedTask);
        Assert.Equal(2, server.Calls.Count);
        c.Stop();
        Assert.Equal(0, clock.ArmedTimers);

        // checked an hour ago (the last result kept in the file): the first check waits until it is due
        var file = Path.Combine(TempDir(), UpdateChecker.FileName);
        await Checker(new FakeUpdateSearch(), file: file, time: clock).CheckAsync();
        clock.Advance(TimeSpan.FromHours(1));
        var fresh = new FakeUpdateSearch();
        var later = Checker(fresh, file: file, time: clock);
        later.Start();
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(later.CurrentCheck);
        clock.Advance(TimeSpan.FromHours(5) - TimeSpan.FromMinutes(10));
        await fresh.WaitForCallAsync();
        await (later.CurrentCheck ?? Task.CompletedTask);
        later.Stop();

        // periodic checks off: nothing scheduled, Check now still works
        var jobs = new FakeJobRegistry();
        var off = new FakeUpdateSearch();
        var disabled = Checker(off, time: clock, jobs: jobs, options: new AdminUpdateCheckOptions { Enabled = false });
        disabled.Start();
        Assert.Equal(0, clock.ArmedTimers);
        Assert.Empty(jobs.Registered);
        await disabled.CheckAsync();
        Assert.Single(off.Calls);
        Assert.Throws<ArgumentException>(() => new AdminUpdateCheckOptions { Interval = TimeSpan.Zero }.Validate());
    }

    [Fact]
    public void NotificationKeys()
    {
        static CliUpdateItem Item(string kind, string name, string package, string source) =>
            new() { Kind = kind, Name = name, Package = package, Title = name, Source = source };
        Assert.Equal($"update:dashboard:{Tickets}", UpdateChecker.NotificationKey(Item("dashboard", Tickets, Tickets, "marketplace")));
        Assert.Equal("update:server:weather", UpdateChecker.NotificationKey(Item("server", "weather", "p", "marketplace")));
        Assert.Equal($"update:code:dashboard:{JobsModule}", UpdateChecker.NotificationKey(Item("dashboard", JobsModule, JobsModule, "code")));
        Assert.Equal("update:code:server:Qodalis.Cli.Plugin.Jobs", UpdateChecker.NotificationKey(Item("server", "jobs", "Qodalis.Cli.Plugin.Jobs", "code")));
        Assert.Equal("update:core:Qodalis.Cli", UpdateChecker.NotificationKey(Item("core", "Qodalis.Cli", "Qodalis.Cli", "nuget")));
    }

    // ---------------------------------------------------------------- the API

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string? token)
    {
        var req = new HttpRequestMessage(method, path);
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(req);
    }

    private static async Task<JsonNode> Json(HttpResponseMessage res, int status = 200)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(status == (int)res.StatusCode, $"expected {status}, got {(int)res.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    [Fact]
    public async Task Api_FindsANewerDashboardPluginAndAPageAddedInCode_UpdateClearsIt()
    {
        var dataDir = TempDir();
        var net = new FakeMarketplaceNet();
        net.Publish(Tickets, [new FakeVersion("1.0.0")], title: "Tickets");
        net.Publish(JobsModule, [new FakeVersion("1.0.0"), new FakeVersion("1.3.0")], title: "Jobs");
        var h = await _servers.StartAsync(net, configure: cli =>
        {
            cli.ConfigureServerPlugins(o => o.DataDir = dataDir);
            cli.AddDashboardExtension(new CliDashboardExtension { Id = "jobs", Title = "Jobs", Entry = "/qcli/modules/jobs/extension.js", Package = JobsModule, Version = "1.0.0" });
        });
        Assert.Equal(201, (int)(await h.Post(new { name = Tickets })).StatusCode);

        // before any check
        var before = await Json(await h.Get(Url));
        Assert.Equal((null, 0, 0), (before["checkedAt"]?.GetValue<string>(), before["count"]!.GetValue<int>(), before["dashboardPlugins"]!.AsArray().Count));
        Assert.Null(before["server"]);

        net.Publish(Tickets, [new FakeVersion("1.0.0"), new FakeVersion("1.1.0")], title: "Tickets");
        var checkedResult = await Json(await h.Send(HttpMethod.Post, $"{Url}/check"));
        var tickets = checkedResult["dashboardPlugins"]!.AsArray().Single()!;
        Assert.Equal((Tickets, "1.0.0", "1.1.0", "marketplace", true), (tickets["name"]!.GetValue<string>(), tickets["current"]!.GetValue<string>(),
            tickets["latest"]!.GetValue<string>(), tickets["source"]!.GetValue<string>(), tickets["canUpdate"]!.GetValue<bool>()));
        var code = checkedResult["codePlugins"]!.AsArray().Single()!;
        Assert.Equal((JobsModule, "1.3.0", "code", $"npm install {JobsModule}@1.3.0"), (code["name"]!.GetValue<string>(), code["latest"]!.GetValue<string>(),
            code["source"]!.GetValue<string>(), code["install"]!["node"]!.GetValue<string>()));
        Assert.Equal(2, checkedResult["count"]!.GetValue<int>());
        // the NuGet feed has no Qodalis.Cli here: the server item without a newest version
        Assert.Equal(("core", false), (checkedResult["server"]!["kind"]!.GetValue<string>(), checkedResult["server"]!["updateAvailable"]!.GetValue<bool>()));
        var get = await h.Get(Url);
        Assert.Equal("no-store", get.Headers.CacheControl?.ToString());
        var got = await Json(get);
        Assert.Equal((2, false), (got["count"]!.GetValue<int>(), got["checking"]!.GetValue<bool>()));
        Assert.True(File.Exists(Path.Combine(dataDir, UpdateChecker.FileName)));

        var notes = (await Json(await h.Get(Qodalis.Cli.Abstractions.Notifications.CliNotificationConstants.Path)))["items"]!.AsArray()
            .Select(n => n!["key"]!.GetValue<string>()).ToList();
        Assert.Contains($"update:dashboard:{Tickets}", notes);
        Assert.Contains($"update:code:dashboard:{JobsModule}", notes);

        // updated from the dashboard: gone from the list, its notification resolved
        Assert.Equal(200, (int)(await h.Put(Uri.EscapeDataString(Tickets))).StatusCode);
        var after = await Json(await h.Get(Url));
        Assert.Equal((1, 0), (after["count"]!.GetValue<int>(), after["dashboardPlugins"]!.AsArray().Count));
        Assert.Null(h.Service<CliNotificationService>().Get($"update:dashboard:{Tickets}"));
    }

    [Fact]
    public async Task Api_NeedsTheAdminSignIn_404WithoutTheMarketplace()
    {
        var h = await _servers.StartAsync(configure: cli => cli.ConfigureServerPlugins(o => o.DataDir = TempDir()));
        Assert.Equal(401, (int)(await h.Get(Url, auth: false)).StatusCode);
        Assert.Equal(401, (int)(await h.Client.PostAsync($"{Url}/check", null)).StatusCode);
        var off = await _servers.StartAsync(enabled: false, configure: cli => cli.ConfigureServerPlugins(o => o.DataDir = TempDir()));
        Assert.Equal(404, (int)(await off.Get(Url)).StatusCode);
        Assert.Null(off.Server.Services.GetService<UpdateChecker>());
    }

    [Fact]
    public async Task Api_ProtectedServer_UpdatesReadOrPluginsInstall_TheUpdateNotificationsOnlyForThem()
    {
        var dataDir = TempDir();
        var net = new FakeMarketplaceNet();
        var host = await UsersTestHost.StartAsync(o => o.MinPasswordLength = 4,
            configureCli: cli =>
            {
                cli.ConfigureServerPlugins(o => o.DataDir = dataDir);
                cli.AddAdmin(o => o.Marketplace = new DashboardMarketplaceOptions { Url = FakeMarketplaceNet.Market, HttpHandler = net, NuGetFeed = FakeNuGetFeed.Feed });
                cli.AddMcp();
            },
            configureApp: app => app.UseQodalisAdmin());
        _hosts.Add(host);
        var root = await host.RootTokenAsync();
        var tokens = new Dictionary<string, string>
        {
            ["viewer"] = await host.UserWithPermissionsAsync(root, "viewer", "admin:access", "commands:execute"),
            ["reader"] = await host.UserWithPermissionsAsync(root, "reader", "admin:access", "updates:read", "commands:execute"),
            ["installer"] = await host.UserWithPermissionsAsync(root, "installer", "admin:access", "plugins:install"),
        };
        Assert.Equal(401, (int)(await Send(host.Client, HttpMethod.Get, Url, null)).StatusCode);
        var refused = await Json(await Send(host.Client, HttpMethod.Get, Url, tokens["viewer"]), 403);
        Assert.Equal("forbidden: missing permission updates:read", refused["error"]!.GetValue<string>());
        await Json(await Send(host.Client, HttpMethod.Post, $"{Url}/check", tokens["viewer"]), 403);
        await Json(await Send(host.Client, HttpMethod.Get, Url, tokens["reader"]));
        await Json(await Send(host.Client, HttpMethod.Get, Url, tokens["installer"]));
        Assert.NotNull((await Json(await Send(host.Client, HttpMethod.Post, $"{Url}/check", tokens["reader"])))["checkedAt"]);

        // the update notifications reach only those who may read the updates
        host.Service<CliNotificationService>().Notify(new CliNotificationInput
        {
            Key = "update:server:x", Kind = "update", Severity = "info", Title = "Update available: X 2.0.0", RequiredPermission = "updates:read",
        });
        async Task<List<string>> Kinds(string who) => (await Json(await Send(host.Client, HttpMethod.Get, CliNotificationConstants.Path, tokens[who])))["items"]!
            .AsArray().Select(n => n!["kind"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("update", await Kinds("viewer"));
        Assert.Contains("update", await Kinds("reader"));

        // updates_list: the same permissions
        var denied = await Mcp.CallAsync(host.Client, "updates_list", null, tokens["viewer"]);
        Assert.True(denied.GetProperty("isError").GetBoolean());
        Assert.Equal("forbidden: missing permission updates:read", Mcp.Text(denied));
        var listed = await Mcp.CallAsync(host.Client, "updates_list", null, tokens["reader"]);
        Assert.False(listed.GetProperty("isError").GetBoolean());
        Assert.StartsWith("0 update(s) available; last checked ", Mcp.Text(listed));
        Assert.Equal(0, listed.GetProperty("structuredContent").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task McpTool_AnswersTheLastResult_OrChecksFirst()
    {
        var net = new FakeMarketplaceNet();
        var h = await _servers.StartAsync(net, configure: cli =>
        {
            cli.ConfigureServerPlugins(o => o.DataDir = TempDir());
            cli.AddMcp();
        });
        var last = await Mcp.CallAsync(h.Client, "updates_list", null, h.Token);
        Assert.Equal("0 update(s) available; last checked never", Mcp.Text(last));
        var checkedNow = await Mcp.CallAsync(h.Client, "updates_list", new { check = true }, h.Token);
        Assert.False(checkedNow.GetProperty("isError").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, checkedNow.GetProperty("structuredContent").GetProperty("checkedAt").ValueKind);
    }

    [Fact]
    public async Task WithTheJobsPluginMounted_TheCheckIsTheSystemJobUpdatesCheck()
    {
        var h = await _servers.StartAsync(configure: cli =>
        {
            cli.ConfigureServerPlugins(o => o.DataDir = TempDir());
            cli.ConfigureJobs(_ => { });
        });
        var job = h.Service<CliJobScheduler>().Jobs[UpdateChecker.JobId];
        Assert.Equal(("updates:check", "system", TimeSpan.FromHours(6), JobStatus.Active), (job.Options.Name, job.Options.Group, job.Options.Interval, job.Status));
        Assert.NotNull(job.NextRunAt);
    }

    // ---------------------------------------------------------------- the jobs scheduler

    [Fact]
    public async Task JobsScheduler_AJobRegisteredAfterStart_IsScheduledAtOnce()
    {
        var clock = new JobTestClock(DateTimeOffset.Parse("2026-11-26T10:00:00Z"));
        using var scheduler = new CliJobScheduler(new InMemoryJobStorageProvider(), new CliEventBus(), NullLogger<CliJobScheduler>.Instance, null, clock);
        await scheduler.StartAsync(CancellationToken.None);
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ICliJobRegistry registry = scheduler;
        Assert.Equal("late", registry.Register(new SignalJob(ran), new CliJobOptions { Id = "late", Name = "late", Interval = TimeSpan.FromSeconds(1) }));
        Assert.True(registry.IsRegistered("late"));
        Assert.NotNull(scheduler.Jobs["late"].NextRunAt);
        clock.Advance(TimeSpan.FromSeconds(1.5));
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Throws<InvalidOperationException>(() => registry.Register(new SignalJob(ran), new CliJobOptions { Id = "late", Interval = TimeSpan.FromSeconds(1) }));
        await scheduler.StopAsync(CancellationToken.None);
    }

    private sealed class SignalJob(TaskCompletionSource ran) : ICliJob
    {
        public Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken)
        {
            ran.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
