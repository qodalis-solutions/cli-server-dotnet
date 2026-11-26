using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Config;
using Qodalis.Cli.Tests.Helpers;

using Host = Qodalis.Cli.Tests.MarketplaceTestHost;

namespace Qodalis.Cli.Tests;

/// <summary>
/// Dashboard plugins from the marketplace (admin plugin): browsing with annotations, the trust policy (verified,
/// listed, off), the dashboard API, downloads from jsDelivr then unpkg checked against the marketplace's sha384 (a
/// mismatch stores nothing), an unreachable marketplace, install / update / remove, live registration and serving at
/// /qcli/modules/&lt;id&gt;/, persistence across a restart, id clashes, path-traversal-safe names, permissions on a
/// protected server, audit and events, and the <c>admin dashboard</c> command. A fake marketplace and CDN: no network.
/// </summary>
public sealed class DashboardMarketplaceTests : IAsyncLifetime
{
    private const string Market = FakeMarketplaceNet.Market;
    private const string Api = Market + "/api/v1/qcli/marketplace";
    private const string Jsdelivr = FakeMarketplaceNet.Jsdelivr;
    private const string Unpkg = FakeMarketplaceNet.Unpkg;
    private const string V1 = "/api/v1/qcli";
    private const string Base = V1 + "/dashboard/marketplace";
    private const string Tickets = "@acme/cli-dashboard-tickets";
    private const string Jobs = "@qodalis/cli-dashboard-module-jobs";

    private readonly MarketplaceTestServers _servers = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _servers.DisposeAsync();

    private string TempDir() => _servers.TempDir();

    private static FakeVersion[] V(params string[] versions) => versions.Select(v => new FakeVersion(v)).ToArray();

    private static string[] Entries(string dir) =>
        Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()! : [];

    private static async Task<JsonNode> Json(HttpResponseMessage res) => JsonNode.Parse(await res.Content.ReadAsStringAsync())!;

    private static async Task<string?> Error(HttpResponseMessage res) => (await Json(res))["error"]?.GetValue<string>();

    private Task<Host> StartAsync(FakeMarketplaceNet? net = null, string? dir = null, string? install = null,
        Action<CliBuilder>? configure = null, bool enabled = true, string? contentRoot = null, bool setDir = true) =>
        _servers.StartAsync(net, dir, install, configure, enabled, contentRoot, setDir);

    private Task<(UsersTestHost Host, FakeMarketplaceNet Net, string Dir, Dictionary<string, string> Tokens)> StartProtectedAsync(
        Dictionary<string, string[]> users) => _servers.StartProtectedAsync(users);

    private static List<string> ExtensionIds(JsonNode body) =>
        body["extensions"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()).ToList();

    private static async Task<JsonObject?> Extension(Host h, string id) =>
        (await Json(await h.Get($"{V1}/dashboard/extensions")))["extensions"]!.AsArray()
        .FirstOrDefault(e => e!["id"]!.GetValue<string>() == id)?.AsObject();

    // ---------------------------------------------------------------- helpers

    [Fact]
    public void DerivesThePageId_OfficialModulesBySuffix_ElseTheExtensionId_ElseThePackageName()
    {
        Assert.Equal("jobs", DashboardMarketplace.PluginId("@qodalis/cli-dashboard-module-jobs"));
        Assert.Equal("data-explorer", DashboardMarketplace.PluginId("@qodalis/cli-dashboard-module-data-explorer", "other"));
        Assert.Equal("helpdesk", DashboardMarketplace.PluginId("@acme/cli-dashboard-tickets", "helpdesk"));
        Assert.Equal("tickets", DashboardMarketplace.PluginId("@acme/cli-dashboard-tickets", "Bad Id"));
        Assert.Equal("crm", DashboardMarketplace.PluginId("@acme/cli-dashboard-plugin-crm"));
        Assert.Equal("my-page-v2", DashboardMarketplace.PluginId("my.page_v2"));
        Assert.Null(DashboardMarketplace.PluginId("___"));
    }

    [Fact]
    public void KeepsNamesInsideTheStorageDirectory_ScopedNamesBecomeScopePlusName_TraversalIsRefused()
    {
        Assert.Equal("@acme+tickets", DashboardMarketplace.DirName("@acme/tickets"));
        Assert.Equal("tickets", DashboardMarketplace.DirName("tickets"));
        foreach (var bad in new[] { "../etc", "..", ".hidden", "@acme/../../x", "a/b", "@acme/x/y", "UPPER", "a\\b", "", new string('x', 215), "@/x", "a\0b", "x\n" })
        {
            Assert.False(DashboardMarketplace.IsValidName(bad), bad);
            Assert.Equal("invalid package name", Assert.Throws<DashboardMarketplaceException>(() => DashboardMarketplace.DirName(bad)).Message);
        }
    }

    [Fact]
    public void ComparesVersionsAsSemver_PrereleasesBeforeTheirRelease()
    {
        var sorted = new[] { "1.0.0", "1.0.0-beta.10", "1.0.0-beta.2", "0.9.9", "1.0.0-alpha", "1.10.0", "1.2.0" }
            .Order(Comparer<string>.Create(DashboardMarketplace.CompareVersions));
        Assert.Equal(["0.9.9", "1.0.0-alpha", "1.0.0-beta.2", "1.0.0-beta.10", "1.0.0", "1.2.0", "1.10.0"], sorted);
    }

    [Fact]
    public void ReadsTheDotnetServerPackageFromEitherForm_Sha384AsSri()
    {
        Assert.Equal("Qodalis.Cli.Plugin.Jobs", DashboardMarketplace.DotnetServerPackage(JsonNode.Parse("""{"node":"@qodalis/cli-server-plugin-jobs","dotnet":"Qodalis.Cli.Plugin.Jobs"}""")));
        Assert.Equal("Y", DashboardMarketplace.DotnetServerPackage(JsonNode.Parse("""[{"ecosystem":"npm","name":"x"},{"ecosystem":"nuget","name":"Y","install":"dotnet add package Y"}]""")));
        Assert.Null(DashboardMarketplace.DotnetServerPackage(null));
        Assert.Null(DashboardMarketplace.DotnetServerPackage(JsonNode.Parse("""{"node":"x"}""")));
        Assert.Equal(FakeMarketplaceNet.IntegrityOf("abc"u8.ToArray()), DashboardMarketplace.Sha384Integrity("abc"u8));
    }

    [Fact]
    public void RefusesAnInvalidMarketplaceUrlOrPolicy()
    {
        Assert.StartsWith("invalid marketplace url", Assert.Throws<ArgumentException>(() => new DashboardMarketplaceOptions { Url = "ftp://x" }.Validate()).Message);
        Assert.StartsWith("invalid marketplace install policy", Assert.Throws<ArgumentException>(() => new DashboardMarketplaceOptions { Install = "everyone" }.Validate()).Message);
        new DashboardMarketplaceOptions { Enabled = false, Url = "ftp://x" }.Validate();
        Assert.Throws<ArgumentException>(() => new DashboardMarketplace(new DashboardMarketplaceOptions { Url = "x" }, "/tmp", new CliDashboardExtensions([]), () => []));
    }

    [Fact]
    public async Task TheDefaultDirectory_IsQcliDataInTheContentRoot()
    {
        var root = TempDir();
        var h = await StartAsync(contentRoot: root, setDir: false);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "qcli-data", "dashboard-plugins"), h.Service<DashboardMarketplace>().Dir);
    }

    // ---------------------------------------------------------------- browsing

    [Fact]
    public async Task GetRoot_ReportsTheMarketplace_ThePolicy_AndTheDashboardApi()
    {
        var h = await StartAsync();
        var res = await h.Get(Base);
        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
        Assert.Equal("""{"marketplaceUrl":"https://market.test","install":"verified","dashboardApi":1}""", await res.Content.ReadAsStringAsync());
        Assert.Equal(401, (int)(await h.Get(Base, auth: false)).StatusCode);
    }

    [Fact]
    public async Task GetPlugins_ProxiesKindDashboard_AndAnnotatesEachItem()
    {
        var net = new FakeMarketplaceNet();
        net.Publish(Jobs, V("1.0.0", "1.1.0"), icon: "⏱️", title: "Jobs", serverPackages: new JsonObject { ["dotnet"] = "Qodalis.Cli.Plugin.Jobs" });
        net.Publish(Tickets, V("2.0.0"), verified: false);
        net.Publish("@acme/cli-dashboard-next", [new FakeVersion("3.0.0", DashboardApi: 2)]);
        net.Publish("@acme/web-thing", V("1.0.0"), kind: "web");
        var h = await StartAsync(net);
        var res = await h.Get($"{Base}/plugins?query=&page=1&pageSize=10");
        Assert.Equal(200, (int)res.StatusCode);
        var listUrl = new Uri(net.Calls.First(c => c.StartsWith($"{Api}/plugins?", StringComparison.Ordinal)));
        var query = System.Web.HttpUtility.ParseQueryString(listUrl.Query);
        Assert.Equal("dashboard", query["kind"]);
        Assert.Equal("1", query["dashboardApi"]);
        Assert.Equal("10", query["pageSize"]);
        var body = await Json(res);
        Assert.Equal(3, body["total"]!.GetValue<int>());
        Assert.Equal(1, body["page"]!.GetValue<int>());
        Assert.Equal(10, body["pageSize"]!.GetValue<int>());
        var byName = body["items"]!.AsArray().ToDictionary(i => i!["name"]!.GetValue<string>(), i => i!.AsObject());
        Assert.Equal(new[] { "@acme/cli-dashboard-next", Jobs, Tickets }.Order(), byName.Keys.Order());
        var jobs = byName[Jobs];
        Assert.Equal("dashboard", jobs["kind"]!.GetValue<string>());
        Assert.True(jobs["publisher"]!["verified"]!.GetValue<bool>());
        Assert.Null(jobs["installed"]);
        Assert.True(jobs.ContainsKey("installed"));
        Assert.False(jobs["updateAvailable"]!.GetValue<bool>());
        Assert.True(jobs["allowed"]!.GetValue<bool>());
        Assert.False(jobs.ContainsKey("blockedReason"));
        Assert.Equal("""{"serverPackage":"Qodalis.Cli.Plugin.Jobs","installed":false}""", jobs["requires"]!.ToJsonString());
        Assert.False(byName[Tickets]["allowed"]!.GetValue<bool>());
        Assert.Equal("unverified publisher", byName[Tickets]["blockedReason"]!.GetValue<string>());
        Assert.True(byName[Tickets].ContainsKey("requires"));
        Assert.Null(byName[Tickets]["requires"]);
        Assert.Equal("needs dashboard API 2", byName["@acme/cli-dashboard-next"]["blockedReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task AMarketplaceIgnoringTheKindFilter_OnlyItsDashboardPlugins_CountedOnTheirOwn()
    {
        var net = new FakeMarketplaceNet { IgnoresKind = true };
        net.Publish(Jobs, V("1.0.0"));
        foreach (var name in new[] { "@acme/web-one", "@acme/web-two", "@acme/web-three" }) net.Publish(name, V("1.0.0"), kind: "web");
        var h = await StartAsync(net);

        var body = await Json(await h.Get($"{Base}/plugins"));
        Assert.Equal([Jobs], body["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()));
        Assert.Equal(1, body["total"]!.GetValue<int>());

        // Only web plugins: an empty list with nothing counted
        lock (net.Plugins) net.Plugins.Remove(Jobs);
        var none = await Json(await h.Get($"{Base}/plugins"));
        Assert.Empty(none["items"]!.AsArray());
        Assert.Equal(0, none["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task RequiresInstalled_FollowsTheServerPluginsTheServerHas()
    {
        var net = new FakeMarketplaceNet();
        net.Publish(Jobs, V("1.0.0"), serverPackages: JsonNode.Parse("""[{"ecosystem":"npm","name":"@qodalis/cli-server-plugin-admin"},{"ecosystem":"nuget","name":"Qodalis.Cli.Plugin.Admin","install":"dotnet add package Qodalis.Cli.Plugin.Admin"}]"""));
        var h = await StartAsync(net);
        var item = (await Json(await h.Get($"{Base}/plugins")))["items"]![0]!;
        Assert.Equal("""{"serverPackage":"Qodalis.Cli.Plugin.Admin","installed":true}""", item["requires"]!.ToJsonString());
    }

    [Fact]
    public async Task InstallListed_AllowsUnverifiedPublishers_InstallOff_BlocksEverything()
    {
        var net = new FakeMarketplaceNet();
        net.Publish(Tickets, V("2.0.0"), verified: false);
        var listed = await StartAsync(net, install: "listed");
        Assert.True((await Json(await listed.Get($"{Base}/plugins")))["items"]![0]!["allowed"]!.GetValue<bool>());
        var off = await StartAsync(net, install: "off");
        Assert.Equal("off", (await Json(await off.Get(Base)))["install"]!.GetValue<string>());
        var item = (await Json(await off.Get($"{Base}/plugins")))["items"]![0]!;
        Assert.False(item["allowed"]!.GetValue<bool>());
        Assert.Equal("install disabled", item["blockedReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetPluginByName_DetailsAndVersions_Annotated_ScopedNamesRawOrEncoded()
    {
        var net = new FakeMarketplaceNet();
        net.Publish(Tickets, [new FakeVersion("1.0.0"), new FakeVersion("1.1.0", Status: "flagged"), new FakeVersion("2.0.0", DashboardApi: 2)]);
        var h = await StartAsync(net);
        foreach (var path in new[] { $"{Base}/plugins/{Tickets}", $"{Base}/plugins/{Uri.EscapeDataString(Tickets)}" })
        {
            var res = await h.Get(path);
            Assert.Equal(200, (int)res.StatusCode);
            var body = await Json(res);
            Assert.Equal(Tickets, body["name"]!.GetValue<string>());
            Assert.Null(body["installed"]);
            Assert.True(body["allowed"]!.GetValue<bool>());
            Assert.False(body["updateAvailable"]!.GetValue<bool>());
            var versions = body["versions"]!.AsArray().ToDictionary(v => v!["version"]!.GetValue<string>(), v => v!);
            Assert.True(versions["1.0.0"]["allowed"]!.GetValue<bool>());
            Assert.Equal(["dist/extension.js", "dist/extension.css"], versions["1.0.0"]["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));
            Assert.False(versions["1.1.0"]["allowed"]!.GetValue<bool>());
            Assert.Equal("version flagged", versions["1.1.0"]["blockedReason"]!.GetValue<string>());
            Assert.Equal("needs dashboard API 2", versions["2.0.0"]["blockedReason"]!.GetValue<string>());
        }
        Assert.Equal("plugin not found: @acme/missing", await Error(await h.Get($"{Base}/plugins/@acme/missing")));
        Assert.Equal(400, (int)(await h.Get($"{Base}/plugins/..%2F..%2Fetc")).StatusCode);
    }

    [Fact]
    public async Task AnUnreachableMarketplace_Is502_MarketplaceUnreachable()
    {
        var net = new FakeMarketplaceNet { MarketplaceDown = true };
        var h = await StartAsync(net);
        foreach (var res in new[] { await h.Get($"{Base}/plugins"), await h.Get($"{Base}/plugins/{Tickets}"), await h.Post(new { name = Tickets }) })
        {
            Assert.Equal(502, (int)res.StatusCode);
            Assert.Equal("""{"error":"marketplace unreachable"}""", await res.Content.ReadAsStringAsync());
        }
        Assert.Empty(Entries(h.Dir));
        // a marketplace answering 500 counts as unreachable too
        var broken = await StartAsync(new FakeMarketplaceNet { MarketplaceStatus = HttpStatusCode.InternalServerError });
        Assert.Equal(502, (int)(await broken.Get($"{Base}/plugins")).StatusCode);
    }

    [Fact]
    public async Task RejectsBadPagingParameters()
    {
        var h = await StartAsync();
        Assert.Equal("invalid page", await Error(await h.Get($"{Base}/plugins?page=0")));
        Assert.Equal("invalid pageSize", await Error(await h.Get($"{Base}/plugins?pageSize=101")));
        Assert.Equal("invalid page", await Error(await h.Get($"{Base}/plugins?page=x")));
    }

    // ---------------------------------------------------------------- install, update, remove

    [Fact]
    public async Task InstallsTheNewestFittingVersion_Verified_Stored_RegisteredLive_AndServedFromThisServer()
    {
        var net = new FakeMarketplaceNet();
        net.Publish(Jobs, [new FakeVersion("1.0.0"), new FakeVersion("1.1.0"), new FakeVersion("2.0.0", DashboardApi: 2), new FakeVersion("1.2.0", Status: "pending")],
            title: "Jobs", icon: "⏱️");
        var h = await StartAsync(net);
        Assert.Null(await Extension(h, "jobs"));

        var res = await h.Post(new { name = Jobs });
        Assert.Equal(201, (int)res.StatusCode);
        var body = (await Json(res)).AsObject();
        Assert.Equal(["name", "version", "title", "icon", "id", "installedAt", "installedBy", "hasConfig"], body.Select(p => p.Key));
        Assert.Equal(Jobs, body["name"]!.GetValue<string>());
        Assert.Equal("1.1.0", body["version"]!.GetValue<string>());
        Assert.Equal("Jobs", body["title"]!.GetValue<string>());
        Assert.Equal("⏱️", body["icon"]!.GetValue<string>());
        Assert.Equal("jobs", body["id"]!.GetValue<string>());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", body["installedAt"]!.GetValue<string>());
        Assert.Equal("admin", body["installedBy"]!.GetValue<string>());

        // stored: <dir>/<safe-name>/<version>/{extension.js,extension.css} and installed.json, no staging leftovers
        Assert.Equal(["extension.css", "extension.js"], Entries(Path.Combine(h.Dir, "@qodalis+cli-dashboard-module-jobs", "1.1.0")));
        Assert.Equal(["@qodalis+cli-dashboard-module-jobs", "installed.json"], Entries(h.Dir));
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(h.Dir, "installed.json")))!["plugins"]!.AsArray();
        var record = Assert.Single(saved)!.AsObject();
        Assert.Equal(["name", "version", "id", "title", "icon", "files", "installedAt", "installedBy"], record.Select(p => p.Key));
        Assert.Equal(body["installedAt"]!.GetValue<string>(), record["installedAt"]!.GetValue<string>());
        Assert.Equal(["extension.js:dist/extension.js", "extension.css:dist/extension.css"],
            record["files"]!.AsArray().Select(f => $"{f!["file"]}:{f["path"]}"));
        Assert.All(record["files"]!.AsArray(), f =>
        {
            Assert.StartsWith("sha384-", f!["integrity"]!.GetValue<string>());
            Assert.True(f["size"]!.GetValue<long>() > 0);
        });
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(h.Dir, "installed.json")));
        // downloaded from jsDelivr (unpkg not needed)
        Assert.Equal([$"{Jsdelivr}/{Jobs}@1.1.0/dist/extension.js", $"{Jsdelivr}/{Jobs}@1.1.0/dist/extension.css"], net.CdnCalls());

        // live in the extensions list, marked as from the marketplace
        var page = (await Extension(h, "jobs"))!;
        Assert.Equal(["id", "title", "icon", "entry", "styles", "version", "source"], page.Select(p => p.Key));
        Assert.Equal("Jobs", page["title"]!.GetValue<string>());
        Assert.Equal("⏱️", page["icon"]!.GetValue<string>());
        Assert.Equal("1.1.0", page["version"]!.GetValue<string>());
        Assert.Equal("marketplace", page["source"]!.GetValue<string>());
        var entry = page["entry"]!.GetValue<string>();
        Assert.Matches(@"^/qcli/modules/jobs/extension\.js\?v=[0-9a-f]{12}$", entry);
        var style = Assert.Single(page["styles"]!.AsArray())!.GetValue<string>();
        Assert.Matches(@"^/qcli/modules/jobs/extension\.css\?v=[0-9a-f]{12}$", style);

        // served from this server's origin, no sign-in, with an ETag
        var js = await h.Get(entry, auth: false);
        Assert.Equal(200, (int)js.StatusCode);
        Assert.Equal("text/javascript", js.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache", js.Headers.CacheControl?.ToString());
        Assert.Contains("version: '1.1.0'", await js.Content.ReadAsStringAsync());
        var conditional = new HttpRequestMessage(HttpMethod.Get, "/qcli/modules/jobs/extension.js");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", js.Headers.ETag!.ToString());
        Assert.Equal(304, (int)(await h.Client.SendAsync(conditional)).StatusCode);
        Assert.Equal("text/css", (await h.Get(style, auth: false)).Content.Headers.ContentType!.MediaType);
        Assert.Equal(404, (int)(await h.Get("/qcli/modules/jobs/installed.json", auth: false)).StatusCode);
        Assert.Equal(404, (int)(await h.Get("/qcli/modules/jobs/..%2F..%2Finstalled.json", auth: false)).StatusCode);
        Assert.Equal(404, (int)(await h.Get("/qcli/modules/other/extension.js", auth: false)).StatusCode);

        // audit and event
        var audit = Assert.Single(h.Audit.Entries);
        Assert.Equal("dashboard.plugin.install", audit.Event);
        Assert.Equal("admin", audit.Username);
        Assert.Equal("admin", audit.Actor);
        Assert.Equal(Jobs, audit.Target);
        Assert.Equal($"{Jobs}@1.1.0", audit.Details);
        Assert.Equal("name=@qodalis/cli-dashboard-module-jobs,version=1.1.0,id=jobs", string.Join(',', audit.Properties!.Select(p => $"{p.Key}={p.Value}")));
        var e = Assert.Single(h.Events);
        Assert.Equal("dashboard:extensions-changed", e.Type);
        Assert.Equal("admin:access", e.Audience.Permission);
        Assert.Equal("action=install,name=@qodalis/cli-dashboard-module-jobs,version=1.1.0,id=jobs", Data(e));

        // GET /installed, the list item now annotated
        var installed = await Json(await h.Get($"{Base}/installed"));
        var expected = body.DeepClone().AsObject();
        expected["updateAvailable"] = false;
        Assert.Equal(new JsonArray(expected).ToJsonString(), installed.ToJsonString());
        Assert.Equal("1.1.0", (await Json(await h.Get($"{Base}/plugins")))["items"]![0]!["installed"]!["version"]!.GetValue<string>());
    }

    private static string Data(CliEvent e) => string.Join(',', e.Data.Where(p => p.Key != "timestamp").Select(p => $"{p.Key}={p.Value}"));

    [Fact]
    public async Task InstallsAGivenVersion_RefusesUnknownUnlistedInvalidAndTooNewVersions()
    {
        var net = new FakeMarketplaceNet();
        net.Publish(Tickets, [new FakeVersion("1.0.0"), new FakeVersion("1.1.0", Status: "flagged"), new FakeVersion("2.0.0", DashboardApi: 2)]);
        var h = await StartAsync(net);
        Assert.Equal($"version not found: {Tickets}@9.9.9", await Error(await h.Post(new { name = Tickets, version = "9.9.9" })));
        Assert.Equal(404, (int)(await h.Post(new { name = Tickets, version = "1.1.0" })).StatusCode);
        Assert.Equal("needs dashboard API 2", await Error(await h.Post(new { name = Tickets, version = "2.0.0" })));
        Assert.Equal("invalid version", await Error(await h.Post(new { name = Tickets, version = "../1" })));
        Assert.Equal("invalid version", await Error(await h.Post(new { name = Tickets, version = 1 })));
        Assert.Equal("invalid package name", await Error(await h.Post(new { })));
        Assert.Equal("invalid package name", await Error(await h.Post(new { name = 5 })));
        Assert.Equal("plugin not found: @acme/missing", await Error(await h.Post(new { name = "@acme/missing" })));
        var ok = await h.Post(new { name = Tickets, version = "1.0.0" });
        Assert.Equal(201, (int)ok.StatusCode);
        var body = await Json(ok);
        Assert.Equal("1.0.0", body["version"]!.GetValue<string>());
        Assert.Equal("tickets", body["id"]!.GetValue<string>());
        Assert.Equal("cli-dashboard-tickets", body["title"]!.GetValue<string>());
        Assert.True(body.AsObject().ContainsKey("icon"));
        Assert.Null(body["icon"]);
        Assert.Equal($"already installed: {Tickets}@1.0.0", await Error(await h.Post(new { name = Tickets })));
    }

    [Fact]
    public async Task RefusesAPluginNeedingANewerApi_AWebPlugin_AndAVersionWithBadFileRecords()
    {
        var net = new FakeMarketplaceNet();
        net.Publish("@acme/cli-dashboard-next", [new FakeVersion("3.0.0", DashboardApi: 2)]);
        net.Publish("@acme/web-thing", V("1.0.0"), kind: "web");
        net.Publish("@acme/cli-dashboard-bad", V("1.0.0"));
        net.Plugins["@acme/cli-dashboard-bad"]["versions"]![0]!["files"]![0]!["path"] = "../../evil.js";
        var h = await StartAsync(net);
        Assert.Equal("needs dashboard API 2", await Error(await h.Post(new { name = "@acme/cli-dashboard-next" })));
        Assert.Equal("not a dashboard plugin: @acme/web-thing", await Error(await h.Post(new { name = "@acme/web-thing" })));
        var bad = await h.Post(new { name = "@acme/cli-dashboard-bad" });
        Assert.Equal(502, (int)bad.StatusCode);
        Assert.Equal("invalid marketplace record: files", await Error(bad));
        Assert.Empty(Entries(h.Dir));
    }

    [Fact]
    public async Task TrustPolicy_VerifiedRefusesUnverifiedPublishersWith403_ListedAllowsThem_OffRefusesAll()
    {
        var net = new FakeMarketplaceNet();
        net.Publish(Tickets, V("1.0.0"), verified: false);
        var verified = await StartAsync(net);
        var refused = await verified.Post(new { name = Tickets });
        Assert.Equal(403, (int)refused.StatusCode);
        Assert.Equal("publisher not verified", await Error(refused));
        Assert.Empty(Entries(verified.Dir));
        Assert.Empty(net.CdnCalls());

        var listed = await StartAsync(net, install: "listed");
        Assert.Equal(201, (int)(await listed.Post(new { name = Tickets })).StatusCode);

        var off = await StartAsync(net, install: "off");
        var disabled = await off.Post(new { name = Tickets });
        Assert.Equal(403, (int)disabled.StatusCode);
        Assert.Equal("install disabled", await Error(disabled));
    }

    [Fact]
    public async Task AnIntegrityMismatchOnEveryCdn_Is502_AndStoresNothing()
    {
        var net = new FakeMarketplaceNet();
        net.Publish(Tickets, [new FakeVersion("1.0.0", Jsdelivr: "tampered", Unpkg: "tampered too")]);
        var h = await StartAsync(net);
        var res = await h.Post(new { name = Tickets });
        Assert.Equal(502, (int)res.StatusCode);
        Assert.Equal("integrity mismatch: dist/extension.js", await Error(res));
        Assert.Empty(Entries(h.Dir));
        Assert.Empty(h.Audit.Entries);
        Assert.Empty(h.Events);
        Assert.Null(await Extension(h, "tickets"));
    }

    [Fact]
    public async Task FallsBackToUnpkg_WhenJsdelivrFailsOrServesOtherBytes_BothFailingIsDownloadFailed()
    {
        var net = new FakeMarketplaceNet();
        net.Publish("@acme/cli-dashboard-a", [new FakeVersion("1.0.0", Jsdelivr: null)]);
        net.Publish("@acme/cli-dashboard-b", [new FakeVersion("1.0.0", Jsdelivr: "tampered")]);
        net.Publish("@acme/cli-dashboard-c", [new FakeVersion("1.0.0", Css: null, Jsdelivr: null, Unpkg: null)]);
        var h = await StartAsync(net);
        Assert.Equal(201, (int)(await h.Post(new { name = "@acme/cli-dashboard-a" })).StatusCode);
        Assert.Contains($"{Unpkg}/@acme/cli-dashboard-a@1.0.0/dist/extension.js", net.Calls);
        Assert.Equal(201, (int)(await h.Post(new { name = "@acme/cli-dashboard-b" })).StatusCode);
        Assert.NotEqual("tampered", await File.ReadAllTextAsync(Path.Combine(h.Dir, "@acme+cli-dashboard-b", "1.0.0", "extension.js")));
        var failed = await h.Post(new { name = "@acme/cli-dashboard-c" });
        Assert.Equal(502, (int)failed.StatusCode);
        Assert.Equal("download failed: dist/extension.js", await Error(failed));
        Assert.False(Directory.Exists(Path.Combine(h.Dir, "@acme+cli-dashboard-c")));
    }

    [Fact]
    public async Task RefusesFilesOver5Mb_AsTheRecordSaysOrAsTheCdnServes()
    {
        var net = new FakeMarketplaceNet();
        var big = new string('x', 5 * 1024 * 1024 + 1);
        net.Publish(Tickets, [new FakeVersion("1.0.0", Js: big, Css: null)]);
        // The record claims a small file, the CDNs serve a big one
        net.Publish("@acme/cli-dashboard-liar", [new FakeVersion("1.0.0", Css: null)]);
        net.Plugins["@acme/cli-dashboard-liar"]["versions"]![0]!["files"]![0]!["integrity"] = FakeMarketplaceNet.IntegrityOf(Encoding.UTF8.GetBytes(big));
        net.Files[$"{Jsdelivr}/@acme/cli-dashboard-liar@1.0.0/dist/extension.js"] = Encoding.UTF8.GetBytes(big);
        net.Files[$"{Unpkg}/@acme/cli-dashboard-liar@1.0.0/dist/extension.js"] = Encoding.UTF8.GetBytes(big);
        var h = await StartAsync(net);
        var res = await h.Post(new { name = Tickets });
        Assert.Equal(502, (int)res.StatusCode);
        Assert.Equal("file too large: dist/extension.js", await Error(res));
        Assert.Equal("download failed: dist/extension.js", await Error(await h.Post(new { name = "@acme/cli-dashboard-liar" })));
        Assert.Empty(Entries(h.Dir));
    }

    [Fact]
    public async Task ReadsTheFilesFromTheVersionRecord_WhenTheDetailsLeaveThemOut()
    {
        var net = new FakeMarketplaceNet { FilesOnlyInVersionRecord = true };
        net.Publish(Tickets, [new FakeVersion("1.0.0", Css: null)]);
        var h = await StartAsync(net);
        Assert.Equal(201, (int)(await h.Post(new { name = Tickets })).StatusCode);
        Assert.Contains(net.Calls, c => Uri.UnescapeDataString(c) == $"{Api}/plugins/{Tickets}/versions/1.0.0");
        Assert.Equal(["extension.js"], Entries(Path.Combine(h.Dir, "@acme+cli-dashboard-tickets", "1.0.0")));
    }

    [Fact]
    public async Task AnIdTakenByAnotherExtension_Is409()
    {
        var net = new FakeMarketplaceNet();
        net.Publish(Jobs, V("1.0.0"));
        net.Publish("@acme/cli-dashboard-billing", V("1.0.0"), extensionId: "billing");
        var h = await StartAsync(net, configure: cli => cli.AddDashboardExtension(new CliDashboardExtension { Id = "jobs", Title = "Jobs", Entry = "/qcli/modules/jobs/extension.js" }));
        var res = await h.Post(new { name = Jobs });
        Assert.Equal(409, (int)res.StatusCode);
        Assert.Equal("dashboard extension already registered: jobs", await Error(res));
        Assert.Empty(Entries(h.Dir));
        // the extension's own id; a second package with that id clashes with the first
        Assert.Equal("billing", (await Json(await h.Post(new { name = "@acme/cli-dashboard-billing" })))["id"]!.GetValue<string>());
        net.Publish("@other/billing-page", V("1.0.0"), extensionId: "billing");
        Assert.Equal(409, (int)(await h.Post(new { name = "@other/billing-page" })).StatusCode);
    }

    [Fact]
    public async Task PathTraversalInNames_IsRefusedBeforeAnythingIsFetchedOrWritten()
    {
        var h = await StartAsync();
        foreach (var name in new[] { "../../etc/passwd", "..", "@acme/../x", "a/b/c", "/abs", "C:\\x" })
        {
            var res = await h.Post(new { name });
            Assert.Equal(400, (int)res.StatusCode);
            Assert.Equal("invalid package name", await Error(res));
        }
        Assert.Equal(400, (int)(await h.Put("..%2F..%2Fetc")).StatusCode);
        Assert.Equal(400, (int)(await h.Delete("..%2F..")).StatusCode);
        Assert.Empty(h.Net.Calls);
        Assert.Empty(Entries(h.Dir));
    }

    [Fact]
    public async Task UpdatesToTheNewestVersion_NewFiles_SamePageId_OldVersionDeleted_AuditedAndPublished()
    {
        var net = new FakeMarketplaceNet();
        net.Publish(Tickets, V("1.0.0"));
        var h = await StartAsync(net);
        Assert.Equal(201, (int)(await h.Post(new { name = Tickets })).StatusCode);
        var oldEntry = (await Extension(h, "tickets"))!["entry"]!.GetValue<string>();

        // nothing newer: unchanged
        var same = await h.Put(Tickets);
        Assert.Equal(200, (int)same.StatusCode);
        Assert.Equal("1.0.0", (await Json(same))["version"]!.GetValue<string>());
        Assert.Single(h.Audit.Entries);

        net.Publish(Tickets, V("1.0.0", "1.2.0"));
        Assert.True((await Json(await h.Get($"{Base}/installed")))[0]!["updateAvailable"]!.GetValue<bool>());
        var item = (await Json(await h.Get($"{Base}/plugins")))["items"]![0]!;
        Assert.Equal("1.0.0", item["installed"]!["version"]!.GetValue<string>());
        Assert.True(item["updateAvailable"]!.GetValue<bool>());

        var res = await h.Put(Uri.EscapeDataString(Tickets));
        Assert.Equal(200, (int)res.StatusCode);
        var body = await Json(res);
        Assert.Equal(Tickets, body["name"]!.GetValue<string>());
        Assert.Equal("1.2.0", body["version"]!.GetValue<string>());
        Assert.Equal("tickets", body["id"]!.GetValue<string>());
        Assert.Equal(["1.2.0"], Entries(Path.Combine(h.Dir, "@acme+cli-dashboard-tickets")));
        var pages = (await Json(await h.Get($"{V1}/dashboard/extensions")))["extensions"]!.AsArray().Where(e => e!["id"]!.GetValue<string>() == "tickets").ToList();
        var page = Assert.Single(pages)!;
        Assert.Equal("1.2.0", page["version"]!.GetValue<string>());
        Assert.NotEqual(oldEntry, page["entry"]!.GetValue<string>());
        Assert.Contains("version: '1.2.0'", await (await h.Get("/qcli/modules/tickets/extension.js", auth: false)).Content.ReadAsStringAsync());
        var audit = h.Audit.Entries[1];
        Assert.Equal("dashboard.plugin.update", audit.Event);
        Assert.Equal($"{Tickets} 1.0.0 -> 1.2.0", audit.Details);
        Assert.Equal("1.0.0", audit.Properties!["previousVersion"]);
        Assert.Equal(["install", "update"], h.Events.Select(e => e.Data["action"]!.ToString()));
        Assert.Equal($"action=update,name={Tickets},version=1.2.0,id=tickets,previousVersion=1.0.0", Data(h.Events[1]));
        Assert.Equal(["1.2.0"], JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(h.Dir, "installed.json")))!["plugins"]!.AsArray()
            .Select(p => p!["version"]!.GetValue<string>()));

        // back to a given version
        Assert.Equal("1.0.0", (await Json(await h.Put(Tickets, new { version = "1.0.0" })))["version"]!.GetValue<string>());
        // a failed update keeps the installed version
        net.Publish(Tickets, [new FakeVersion("1.0.0"), new FakeVersion("1.3.0", Jsdelivr: "bad", Unpkg: "bad")]);
        Assert.Equal(502, (int)(await h.Put(Tickets)).StatusCode);
        Assert.Equal("1.0.0", (await Json(await h.Get($"{Base}/installed")))[0]!["version"]!.GetValue<string>());
        Assert.Equal(200, (int)(await h.Get("/qcli/modules/tickets/extension.js", auth: false)).StatusCode);
        Assert.Equal("not installed: @acme/other", await Error(await h.Put("@acme/other")));
    }

    [Fact]
    public async Task RemovesAPlugin_PageUnregistered_FilesDeleted_AuditedAndPublished()
    {
        var net = new FakeMarketplaceNet();
        net.Publish(Tickets, V("1.0.0"));
        net.Publish(Jobs, V("1.0.0"));
        var h = await StartAsync(net);
        Assert.Equal(201, (int)(await h.Post(new { name = Tickets })).StatusCode);
        Assert.Equal(201, (int)(await h.Post(new { name = Jobs })).StatusCode);
        var res = await h.Delete(Tickets);
        Assert.Equal(200, (int)res.StatusCode);
        var body = await Json(res);
        Assert.Equal("1.0.0", body["version"]!.GetValue<string>());
        Assert.Equal("tickets", body["id"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(h.Dir, "@acme+cli-dashboard-tickets")));
        Assert.True(File.Exists(Path.Combine(h.Dir, "@qodalis+cli-dashboard-module-jobs", "1.0.0", "extension.js")));
        var ids = ExtensionIds(await Json(await h.Get($"{V1}/dashboard/extensions")));
        Assert.Contains("jobs", ids);
        Assert.DoesNotContain("tickets", ids);
        Assert.Equal(404, (int)(await h.Get("/qcli/modules/tickets/extension.js", auth: false)).StatusCode);
        Assert.Equal(["dashboard.plugin.install", "dashboard.plugin.install", "dashboard.plugin.remove"], h.Audit.Entries.Select(a => a.Event));
        Assert.Equal($"action=remove,name={Tickets},version=1.0.0,id=tickets", Data(h.Events[2]));
        Assert.Equal([Jobs], JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(h.Dir, "installed.json")))!["plugins"]!.AsArray()
            .Select(p => p!["name"]!.GetValue<string>()));
        Assert.Equal($"not installed: {Tickets}", await Error(await h.Delete(Tickets)));
    }

    [Fact]
    public async Task ConcurrentInstallsOfOnePlugin_OneWins_TheOtherIs409()
    {
        var net = new FakeMarketplaceNet();
        net.Publish(Tickets, V("1.0.0"));
        var h = await StartAsync(net);
        var results = await Task.WhenAll(h.Post(new { name = Tickets }), h.Post(new { name = Tickets }));
        Assert.Equal([201, 409], results.Select(r => (int)r.StatusCode).Order());
    }

    // ---------------------------------------------------------------- restart

    [Fact]
    public async Task ReloadsInstalledPluginsOnStart_Offline_AndServesThemAgain()
    {
        var net = new FakeMarketplaceNet();
        net.Publish(Tickets, V("1.0.0"), icon: "🎫", title: "Tickets");
        var first = await StartAsync(net);
        Assert.Equal(201, (int)(await first.Post(new { name = Tickets })).StatusCode);

        var offline = new FakeMarketplaceNet { MarketplaceDown = true };
        var second = await StartAsync(offline, dir: first.Dir);
        var page = (await Extension(second, "tickets"))!;
        Assert.Equal("Tickets", page["title"]!.GetValue<string>());
        Assert.Equal("🎫", page["icon"]!.GetValue<string>());
        Assert.Equal("1.0.0", page["version"]!.GetValue<string>());
        Assert.Equal("marketplace", page["source"]!.GetValue<string>());
        Assert.Equal(200, (int)(await second.Get(page["entry"]!.GetValue<string>(), auth: false)).StatusCode);
        // installed still listed; updateAvailable left out while the marketplace is unreachable
        var installed = Assert.Single((await Json(await second.Get($"{Base}/installed"))).AsArray())!.AsObject();
        Assert.Equal(["name", "version", "title", "icon", "id", "installedAt", "installedBy", "hasConfig"], installed.Select(p => p.Key));
        Assert.Equal("admin", installed["installedBy"]!.GetValue<string>());
        // and removable without the marketplace
        Assert.Equal(200, (int)(await second.Delete(Tickets)).StatusCode);
        Assert.Empty(offline.CdnCalls());
    }

    [Fact]
    public async Task SkipsPluginsWhoseFilesChangedOnDiskOrWhoseIdIsTaken_AndSurvivesABrokenInstalledJson()
    {
        var net = new FakeMarketplaceNet();
        net.Publish(Tickets, V("1.0.0"));
        net.Publish(Jobs, V("1.0.0"));
        var first = await StartAsync(net);
        Assert.Equal(201, (int)(await first.Post(new { name = Tickets })).StatusCode);
        Assert.Equal(201, (int)(await first.Post(new { name = Jobs })).StatusCode);
        await File.WriteAllTextAsync(Path.Combine(first.Dir, "@acme+cli-dashboard-tickets", "1.0.0", "extension.js"), "alert(\"tampered\")");

        var second = await StartAsync(net, dir: first.Dir,
            configure: cli => cli.AddDashboardExtension(new CliDashboardExtension { Id = "jobs", Title = "Host jobs", Entry = "/jobs.js" }));
        var extensions = (await Json(await second.Get($"{V1}/dashboard/extensions")))["extensions"]!.AsArray();
        Assert.DoesNotContain(extensions, e => e!["source"] != null);
        Assert.Equal("Host jobs", extensions.First(e => e!["id"]!.GetValue<string>() == "jobs")!["title"]!.GetValue<string>());
        Assert.Equal(404, (int)(await second.Get("/qcli/modules/tickets/extension.js", auth: false)).StatusCode);

        await File.WriteAllTextAsync(Path.Combine(first.Dir, "installed.json"), "{ not json");
        var third = await StartAsync(net, dir: first.Dir);
        Assert.Equal("[]", (await Json(await third.Get($"{Base}/installed"))).ToJsonString());
    }

    [Fact]
    public async Task TurnedOff_ServesNoMarketplaceRoutes_HasNoCommand_AndLoadsNothing()
    {
        var net = new FakeMarketplaceNet();
        net.Publish(Tickets, V("1.0.0"));
        var first = await StartAsync(net);
        Assert.Equal(201, (int)(await first.Post(new { name = Tickets })).StatusCode);

        var off = await StartAsync(net, dir: first.Dir, enabled: false);
        Assert.Equal(404, (int)(await off.Get(Base)).StatusCode);
        Assert.Equal(404, (int)(await off.Post(new { name = Tickets })).StatusCode);
        Assert.DoesNotContain(off.Service<ICliCommandRegistry>().FindProcessor("admin")!.Processors!, p => p.Command == "dashboard");
        Assert.Null(await Extension(off, "tickets"));
        Assert.Equal(404, (int)(await off.Get("/qcli/modules/tickets/extension.js", auth: false)).StatusCode);
    }

    // ---------------------------------------------------------------- permissions on a protected server

    [Fact]
    public async Task OnAProtectedServer_ReadingNeedsAdminAccess_ChangesNeedDashboardInstall()
    {
        var (h, net, _, tokens) = await StartProtectedAsync(new()
        {
            ["viewer"] = ["admin:access"],
            ["installer"] = ["admin:access", "dashboard:install"],
            ["outsider"] = ["files:read"],
        });
        net.Publish(Tickets, V("1.0.0"));
        Assert.Equal(401, (int)(await h.GetAsync(Base)).StatusCode);
        Assert.Equal(403, (int)(await h.GetAsync(Base, tokens["outsider"])).StatusCode);
        Assert.Equal(200, (int)(await h.GetAsync($"{Base}/plugins", tokens["viewer"])).StatusCode);
        Assert.Equal(200, (int)(await h.GetAsync($"{Base}/installed", tokens["viewer"])).StatusCode);

        var denied = await h.PostAsync($"dashboard/marketplace/installed", tokens["viewer"], new { name = Tickets });
        Assert.Equal(403, (int)denied.StatusCode);
        Assert.Contains("dashboard:install", await denied.Content.ReadAsStringAsync());
        Assert.Equal(403, (int)(await h.SendAsync(HttpMethod.Put, $"{Base}/installed/{Tickets}", tokens["viewer"], new { })).StatusCode);
        Assert.Equal(403, (int)(await h.SendAsync(HttpMethod.Delete, $"{Base}/installed/{Tickets}", tokens["viewer"])).StatusCode);

        var ok = await h.PostAsync("dashboard/marketplace/installed", tokens["installer"], new { name = Tickets });
        Assert.Equal(201, (int)ok.StatusCode);
        Assert.Equal("installer", (await Json(ok))["installedBy"]!.GetValue<string>());
        Assert.Equal(200, (int)(await h.SendAsync(HttpMethod.Delete, $"{Base}/installed/{Tickets}", tokens["root"])).StatusCode);
        // the users plugin's audit log has both
        var audit = await h.Service<IAuditStore>().GetEntriesAsync();
        Assert.Contains(audit, a => a.Event == "dashboard.plugin.install" && a.Actor == "installer" && a.ActorId != null);
        Assert.Contains(audit, a => a.Event == "dashboard.plugin.remove" && a.Actor == "root");
        // dashboard:install is in the catalog
        var catalog = (await Json(await h.GetAsync($"{V1}/permissions", tokens["root"]))).AsArray();
        Assert.Contains(catalog, p => p!.ToJsonString() ==
            """{"name":"dashboard:install","description":"Install, update and remove dashboard plugins from the marketplace","group":"Server","surface":"cli"}""");
    }

    [Fact]
    public async Task OnAProtectedServer_TheFilesOfInstalledPluginsAreServedWithoutSignIn()
    {
        var (h, net, _, tokens) = await StartProtectedAsync([]);
        net.Publish(Tickets, V("1.0.0"));
        Assert.Equal(201, (int)(await h.PostAsync("dashboard/marketplace/installed", tokens["root"], new { name = Tickets })).StatusCode);
        Assert.Equal(200, (int)(await h.GetAsync("/qcli/modules/tickets/extension.js")).StatusCode);
    }

    [Fact]
    public async Task OnAProtectedServer_TheCommand_InstallUpdateRemoveNeedDashboardInstall_ListAndSearchAdminAccess()
    {
        var (h, net, _, tokens) = await StartProtectedAsync(new()
        {
            ["viewer"] = ["admin:access", "commands:execute"],
            ["installer"] = ["admin:access", "commands:execute", "dashboard:install"],
        });
        net.Publish(Tickets, V("1.0.0"));
        var registry = h.Service<ICliCommandRegistry>();
        var dashboard = registry.FindProcessor("admin")!.Processors!.Single(p => p.Command == "dashboard");
        Assert.Equal(["list -", "search -", "info -", "install dashboard:install", "update dashboard:install", "remove dashboard:install"],
            dashboard.Processors!.Select(p => $"{p.Command} {(p.RequiredPermissions == null ? "-" : string.Join(",", p.RequiredPermissions))}"));
        Assert.Equal(200, (await ConfigCommands.RunAsync(h.Client, registry, "admin dashboard search", tokens["viewer"])).Status);
        Assert.Equal(403, (await ConfigCommands.RunAsync(h.Client, registry, $"admin dashboard install {Tickets}", tokens["viewer"])).Status);
        var installed = await ConfigCommands.RunAsync(h.Client, registry, $"admin dashboard install {Tickets}", tokens["installer"]);
        Assert.Equal($"Installed {Tickets}@1.0.0: the dashboard page \"cli-dashboard-tickets\" (/x/tickets).", installed.Text);
        Assert.Equal("installer", (await Json(await h.GetAsync($"{Base}/installed", tokens["root"])))[0]!["installedBy"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- the admin dashboard command

    [Fact]
    public async Task TheCommand_ListSearchInstallNameAtVersionUpdateRemove()
    {
        var net = new FakeMarketplaceNet();
        net.Publish(Tickets, V("1.0.0", "1.1.0"), title: "Tickets", serverPackages: new JsonObject { ["dotnet"] = "Acme.Cli.Plugin.Tickets" });
        net.Publish("@other/cli-dashboard-crm", V("0.1.0"), verified: false);
        var h = await StartAsync(net);

        Assert.Equal($"No dashboard plugins installed (marketplace {Market}, install: verified; admin dashboard search <query> finds some).", await h.Run("admin dashboard"));
        Assert.Equal(string.Join('\n',
            "Package                      Version  Publisher         Status",
            $"{Tickets}  1.1.0    @acme (verified)  installable; needs server plugin Acme.Cli.Plugin.Tickets",
            "@other/cli-dashboard-crm     0.1.0    @other            blocked: unverified publisher"), await h.Run("admin dashboard search"));
        Assert.Equal("No dashboard plugins match \"nothing-like-this\".", await h.Run("admin dashboard search nothing-like-this"));

        Assert.Equal($"Installed {Tickets}@1.0.0: the dashboard page \"Tickets\" (/x/tickets).", await h.Run($"admin dashboard install {Tickets}@1.0.0"));
        Assert.Equal($"Error: already installed: {Tickets}@1.0.0", await h.Run($"admin dashboard install {Tickets}"));
        Assert.Equal("Error: publisher not verified", await h.Run("admin dashboard install @other/cli-dashboard-crm"));
        Assert.Equal("Error: invalid package name", await h.Run("admin dashboard install ../../etc"));
        Assert.Equal("Error: name one package: admin dashboard install <name>[@version]", await h.Run("admin dashboard install a b"));

        var list = (await h.Run("admin dashboard list")).Split('\n');
        Assert.Equal($"1 dashboard plugin(s) from {Market} (install: verified)", list[0]);
        Assert.Equal("ID       Package                      Version                   Installed            By", list[1]);
        Assert.Matches($@"^tickets  {Regex.Escape(Tickets)}  1\.0\.0 \(update available\)  \d{{4}}-\d\d-\d\d \d\d:\d\d:\d\d  admin$", list[2]);
        var json = JsonNode.Parse(await h.Run("admin dashboard --json"))!.AsArray();
        Assert.Equal(Tickets, json[0]!["name"]!.GetValue<string>());
        Assert.Equal("1.0.0", json[0]!["version"]!.GetValue<string>());
        Assert.True(json[0]!["updateAvailable"]!.GetValue<bool>());
        Assert.Contains("installed 1.0.0, update available", await h.Run("admin dashboard search tickets"));

        Assert.Equal($"Updated {Tickets} from 1.0.0 to 1.1.0.", await h.Run($"admin dashboard update {Tickets}"));
        Assert.Equal($"{Tickets} is already at 1.1.0, the newest version.", await h.Run($"admin dashboard update {Tickets}"));
        Assert.Equal("Error: not installed: @acme/none", await h.Run("admin dashboard update @acme/none"));
        Assert.Equal($"Removed {Tickets}@1.1.0 (the page tickets).", await h.Run($"admin dashboard remove {Tickets}"));
        Assert.Equal($"Error: not installed: {Tickets}", await h.Run($"admin dashboard remove {Tickets}"));
        Assert.Equal(["dashboard.plugin.install admin", "dashboard.plugin.update admin", "dashboard.plugin.remove admin"],
            h.Audit.Entries.Select(a => $"{a.Event} {a.Username}"));
    }

    [Fact]
    public async Task TheCommand_ReportsAnUnreachableMarketplace()
    {
        var h = await StartAsync(new FakeMarketplaceNet { MarketplaceDown = true });
        Assert.Equal("Error: marketplace unreachable", await h.Run("admin dashboard search"));
        Assert.Equal("Error: marketplace unreachable", await h.Run($"admin dashboard install {Tickets}"));
    }
}
