using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Abstractions.Restart;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Plugins;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Config;

namespace Qodalis.Cli.Tests;

/// <summary>The marketplace's server plugins and a NuGet flat container, in memory; .nupkg files built by the tests.</summary>
internal sealed class FakeServerMarketplaceNet : HttpMessageHandler
{
    public const string Market = "https://market.test";
    public const string Feed = "https://nuget.test/v3-flatcontainer";

    public readonly Dictionary<string, JsonObject> Plugins = new();
    public readonly ConcurrentDictionary<string, byte[]> Packages = new(StringComparer.OrdinalIgnoreCase);
    public readonly ConcurrentQueue<string> Calls = new();

    public static string Sha512Of(byte[] bytes) => $"sha512-{Convert.ToBase64String(SHA512.HashData(bytes))}";

    /// <summary>A .nupkg: its nuspec (with one dependency group for net10.0) and <c>lib/&lt;tfm&gt;/</c> files.</summary>
    public static byte[] Nupkg(string id, string version, Dictionary<string, byte[]>? lib = null, IEnumerable<(string Id, string Range)>? dependencies = null,
        string tfm = "net10.0", string? nuspecId = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var deps = string.Join("", (dependencies ?? []).Select(d => $"<dependency id=\"{d.Id}\" version=\"{d.Range}\" />"));
            var nuspec = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata><id>{nuspecId ?? id}</id><version>{version}</version><authors>t</authors><description>d</description>
                    <dependencies><group targetFramework=".NETStandard2.0" /><group targetFramework="{tfm}">{deps}</group></dependencies>
                  </metadata>
                </package>
                """;
            using (var w = new StreamWriter(zip.CreateEntry($"{id}.nuspec").Open())) w.Write(nuspec);
            foreach (var (name, bytes) in lib ?? [])
            {
                using var s = zip.CreateEntry($"lib/{tfm}/{name}").Open();
                s.Write(bytes);
            }
        }
        return buffer.ToArray();
    }

    public void AddPackage(string id, string version, byte[] bytes) => Packages[$"{id.ToLowerInvariant()}/{version.ToLowerInvariant()}"] = bytes;

    public void Publish(string name, string package, IEnumerable<JsonObject> versions, bool verified = true, JsonObject? extra = null)
    {
        var plugin = new JsonObject
        {
            ["name"] = name,
            ["kind"] = "server",
            ["title"] = name[..1].ToUpperInvariant() + name[1..],
            ["description"] = $"The {name} plugin",
            ["icon"] = "🧪",
            ["publisher"] = new JsonObject { ["name"] = "Qodalis", ["verified"] = verified },
            ["platforms"] = new JsonObject
            {
                ["dotnet"] = new JsonObject { ["package"] = package },
                ["node"] = new JsonObject { ["package"] = $"@qodalis/cli-server-plugin-{name}" },
            },
            ["versions"] = new JsonArray([.. versions.Select(v => (JsonNode)v)]),
        };
        foreach (var (k, v) in extra ?? []) plugin[k] = v?.DeepClone();
        Plugins[name] = plugin;
    }

    public static JsonObject Version(string version, byte[]? bytes = null, string? range = null, JsonArray? dependencies = null, string? integrity = null,
        string status = "listed", bool? autoInstall = null)
    {
        var v = new JsonObject { ["platform"] = "dotnet", ["version"] = version, ["status"] = status };
        if (integrity != null || bytes != null) v["integrity"] = integrity ?? Sha512Of(bytes!);
        if (range != null) v["serverRange"] = range;
        if (dependencies != null) v["dependencies"] = dependencies;
        if (autoInstall != null)
        {
            v["autoInstall"] = autoInstall;
            v["autoInstallReason"] = "needs the host's database";
        }
        return v;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Calls.Enqueue(url);
        var path = request.RequestUri!.AbsolutePath;
        if (url.StartsWith(Market, StringComparison.Ordinal))
        {
            const string api = "/api/v1/qcli/marketplace/plugins";
            if (path == api)
            {
                var items = new JsonArray();
                foreach (var p in Plugins.Values)
                {
                    var item = (JsonObject)p.DeepClone();
                    var latest = (item["versions"] as JsonArray)!.OfType<JsonObject>().Select(v => v["version"]!.GetValue<string>()).LastOrDefault();
                    item.Remove("versions");
                    item["platforms"]!["dotnet"]!["latest"] = latest;
                    item["latest"] = latest;
                    item["compatible"] = true;
                    items.Add(item);
                }
                return Task.FromResult(Json(HttpStatusCode.OK, new JsonObject { ["items"] = items, ["total"] = items.Count, ["page"] = 1, ["pageSize"] = 50 }));
            }
            var name = Uri.UnescapeDataString(path[(api.Length + 1)..]);
            // As the marketplace: without kind=server (the default is web) a server plugin is not found
            if (System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["kind"] != "server")
                return Task.FromResult(Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "plugin not found", ["kind"] = "server" }));
            return Task.FromResult(Plugins.TryGetValue(name, out var plugin)
                ? Json(HttpStatusCode.OK, plugin.DeepClone())
                : Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "not found" }));
        }
        if (url.StartsWith(Feed, StringComparison.Ordinal))
        {
            var parts = path["/v3-flatcontainer/".Length..].Split('/');
            if (parts.Length == 2 && parts[1] == "index.json")
            {
                var versions = Packages.Keys.Where(k => k.StartsWith(parts[0] + "/", StringComparison.OrdinalIgnoreCase)).Select(k => (JsonNode)k.Split('/')[1]);
                return Task.FromResult(Json(HttpStatusCode.OK, new JsonObject { ["versions"] = new JsonArray([.. versions]) }));
            }
            if (parts.Length == 3 && Packages.TryGetValue($"{parts[0]}/{parts[1]}", out var bytes))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

[Collection("ServerPlugins")]
public class ServerPluginMarketplaceTests : IAsyncLifetime
{
    private const string Base = ServerPluginMarketplace.ApiPath;
    private const string Fixture = ServerPluginsTests.FixturePackage;

    private readonly List<string> _dirs = [];
    private readonly List<IHost> _hosts = [];
    private readonly FakeServerMarketplaceNet _net = new();
    private readonly List<AuditEntry> _audit = [];
    private readonly List<CliEvent> _events = [];
    private static readonly byte[] FixtureBytes = File.ReadAllBytes(ServerPluginsTests.FixtureDll);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.StopAsync();
            host.Dispose();
        }
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
        var dir = Path.Combine(Path.GetTempPath(), $"qcli-server-market-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private sealed class AuditList(List<AuditEntry> entries) : IAuditStore
    {
        public Task LogAsync(AuditEntry entry)
        {
            lock (entries) entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<List<AuditEntry>> GetEntriesAsync(AuditQueryOptions? options = null) => Task.FromResult(entries.ToList());

        public Task ClearAsync()
        {
            entries.Clear();
            return Task.CompletedTask;
        }
    }

    private async Task<(IHost Host, HttpClient Client, string Token)> StartAsync(string dataDir, string install = "verified",
        IList<string>? trusted = null, bool enabled = true)
    {
        var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(s => s.AddControllers().AddCli(cli =>
            {
                cli.ConfigureServerPlugins(o => o.DataDir = dataDir);
                cli.Services.AddSingleton<IAuditStore>(new AuditList(_audit));
                cli.AddAdmin(o =>
                {
                    o.Username = "admin";
                    o.Password = "pass";
                    o.JwtSecret = "server-marketplace-test-secret-long-enough-for-hmac";
                    o.Marketplace = new DashboardMarketplaceOptions
                    {
                        Enabled = enabled,
                        Url = FakeServerMarketplaceNet.Market,
                        NuGetFeed = FakeServerMarketplaceNet.Feed,
                        Dir = Path.Combine(dataDir, "dashboard-plugins"),
                        HttpHandler = _net,
                        Install = install,
                        TrustedDependencies = trusted ?? [],
                    };
                });
            }));
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseQodalisAdmin();
                app.UseEndpoints(e => e.MapControllers());
                app.UseCli();
            });
        }).StartAsync();
        _hosts.Add(host);
        host.Services.GetRequiredService<ICliEventBus>().Subscribe("server:*", (e, _) =>
        {
            lock (_events) _events.Add(e);
            return Task.CompletedTask;
        });
        var client = host.GetTestClient();
        var login = await client.PostAsJsonAsync("/api/v1/qcli/auth/login", new { username = "admin", password = "pass" });
        var token = JsonNode.Parse(await login.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();
        return (host, client, token);
    }

    private static async Task<(int Status, JsonNode? Body)> Send(HttpClient client, string token, HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) req.Content = JsonContent.Create(body);
        var res = await client.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return ((int)res.StatusCode, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
    }

    /// <summary>The fixture published as <c>fixture</c> 1.0.0 (and 1.1.0), with a dependency the marketplace pins.</summary>
    private void PublishFixture(bool pinDependency = true, string? range = null, string? integrity = null, bool verified = true, bool? autoInstall = null)
    {
        var dep = FakeServerMarketplaceNet.Nupkg("Acme.Dep", "2.0.0", new() { ["Acme.Dep.dll"] = [1, 2, 3], ["Qodalis.Cli.Abstractions.dll"] = [9, 9] });
        _net.AddPackage("Acme.Dep", "2.0.0", dep);
        byte[] Package(string version) => FakeServerMarketplaceNet.Nupkg(Fixture, version,
            new() { [$"{Fixture}.dll"] = FixtureBytes, [$"{Fixture}.xml"] = "<doc/>"u8.ToArray() },
            [("Qodalis.Cli", "[1.0.0-beta.1, )"), ("Acme.Dep", "[2.0.0, 3.0.0)"), ("Microsoft.Extensions.Logging.Abstractions", "10.0.0")]);
        var v1 = Package("1.0.0");
        var v11 = Package("1.1.0");
        _net.AddPackage(Fixture, "1.0.0", v1);
        _net.AddPackage(Fixture, "1.1.0", v11);
        JsonArray? Deps() => pinDependency
            ? [new JsonObject { ["id"] = "Acme.Dep", ["version"] = "2.0.0", ["integrity"] = FakeServerMarketplaceNet.Sha512Of(dep) }]
            : null;
        _net.Publish("fixture", Fixture,
        [
            FakeServerMarketplaceNet.Version("1.0.0", v1, range, Deps(), integrity, autoInstall: autoInstall),
            FakeServerMarketplaceNet.Version("1.1.0", v11, range, Deps(), integrity, autoInstall: autoInstall),
        ], verified);
    }

    private static CliServerPlugins Plugins(IHost host) => host.Services.GetRequiredService<CliServerPlugins>();

    // ---------------------------------------------------------------- reading packages

    [Fact]
    public void NuGetRanges_Versions_AndTargetFrameworks()
    {
        Assert.True(NuGetPackage.SatisfiesNuGetRange("1.0.0-beta.31", "1.0.0-beta.2"));
        Assert.False(NuGetPackage.SatisfiesNuGetRange("1.0.0-beta.1", "1.0.0-beta.2"));
        Assert.True(NuGetPackage.SatisfiesNuGetRange("2.5.0", "[2.0.0, 3.0.0)"));
        Assert.False(NuGetPackage.SatisfiesNuGetRange("3.0.0", "[2.0.0, 3.0.0)"));
        Assert.True(NuGetPackage.SatisfiesNuGetRange("2.0.0", "[2.0.0]"));
        Assert.False(NuGetPackage.SatisfiesNuGetRange("2.0.1", "[2.0.0]"));
        Assert.True(NuGetPackage.SatisfiesNuGetRange("1.0", "(,2.0]"));
        Assert.False(NuGetPackage.SatisfiesNuGetRange("1.0", "[oops"));
        Assert.Equal(0, NuGetPackage.CompareVersions("1.0", "1.0.0.0"));
        Assert.True(NuGetPackage.CompareVersions("1.0.0-beta.10", "1.0.0-beta.9") > 0);
        Assert.Equal("net10.0", NuGetPackage.BestTfm(["netstandard2.0", "net8.0", "net10.0", "net10.0-windows", "net48"]));
        Assert.Equal("net8.0", NuGetPackage.BestTfm(["netstandard2.0", "net8.0", "net99.0"]));
        Assert.Equal("netstandard2.1", NuGetPackage.BestTfm(["netstandard2.1", "netstandard2.0", "net472"]));
        Assert.Null(NuGetPackage.BestTfm(["net48", "netstandard3.0"]));
        Assert.True(ServerPluginMarketplace.SatisfiesServerRange("1.0.0-beta.31", "^1.0.0-beta.2"));
        Assert.True(ServerPluginMarketplace.SatisfiesServerRange("1.0.0-beta.31", "[1.0.0-beta.2, 2.0.0)"));
        Assert.False(ServerPluginMarketplace.SatisfiesServerRange("1.0.0-beta.31", ">=2.0.0"));
    }

    [Fact]
    public void ThePluginAssembly_IsReadWithoutLoadingIt()
    {
        using var fixture = new MemoryStream(FixtureBytes);
        Assert.Equal(new CliPluginAssemblyInfo(true, true, null), NuGetPackage.ReadPluginAssembly(fixture));
        using var plain = File.OpenRead(typeof(Qodalis.Cli.Abstractions.ICliCommandProcessor).Assembly.Location);
        Assert.False(NuGetPackage.ReadPluginAssembly(plain).HasEntry);
        Assert.Throws<BadImageFormatException>(() => NuGetPackage.ReadPluginAssembly(new MemoryStream([1, 2, 3, 4])));
    }

    // ---------------------------------------------------------------- browsing

    [Fact]
    public async Task Search_AnnotatesThisPlatform_TheOthers_AndWhyItCannotBeInstalled()
    {
        PublishFixture();
        _net.Publish("unverified", "Acme.Unverified", [FakeServerMarketplaceNet.Version("1.0.0", [1])], verified: false);
        var (_, client, token) = await StartAsync(TempDir());

        var (status, info) = await Send(client, token, HttpMethod.Get, Base);
        Assert.Equal(200, status);
        Assert.Equal(("dotnet", true, "verified"), (info!["platform"]!.GetValue<string>(), info["enabled"]!.GetValue<bool>(), info["install"]!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(info["serverVersion"]!.GetValue<string>()));

        var (_, body) = await Send(client, token, HttpMethod.Get, $"{Base}/plugins?query=fix");
        var fixture = body!["items"]!.AsArray().Single(i => i!["name"]!.GetValue<string>() == "fixture")!;
        Assert.Equal(Fixture, fixture["platform"]!["package"]!.GetValue<string>());
        Assert.Equal("1.1.0", fixture["platform"]!["latest"]!.GetValue<string>());
        Assert.Equal(["node"], fixture["otherPlatforms"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.True(fixture["allowed"]!.GetValue<bool>());
        var unverified = body["items"]!.AsArray().Single(i => i!["name"]!.GetValue<string>() == "unverified")!;
        Assert.Equal((false, "unverified publisher"), (unverified["allowed"]!.GetValue<bool>(), unverified["blockedReason"]!.GetValue<string>()));
        Assert.Contains(_net.Calls, c => c.Contains("kind=server&platform=dotnet&serverVersion=", StringComparison.Ordinal));

        var (_, details) = await Send(client, token, HttpMethod.Get, $"{Base}/plugins/fixture");
        Assert.All(details!["versions"]!.AsArray(), v => Assert.True(v!["allowed"]!.GetValue<bool>()));
    }

    // ---------------------------------------------------------------- installing

    [Fact]
    public async Task Install_VerifiesAndExtracts_ThePinnedDependencyToo_NeverTheHostsAssemblies_MarksTheRestart_ThenLoadsAtTheNextStart()
    {
        PublishFixture();
        var dir = TempDir();
        var (host, client, token) = await StartAsync(dir);

        var (status, body) = await Send(client, token, HttpMethod.Post, $"{Base}/installed", new { name = "fixture", version = "1.0.0" });
        Assert.Equal(202, status);
        Assert.Equal(("fixture", Fixture, "1.0.0", "pending", true),
            (body!["name"]!.GetValue<string>(), body["package"]!.GetValue<string>(), body["version"]!.GetValue<string>(),
             body["status"]!.GetValue<string>(), body["restartRequired"]!.GetValue<bool>()));

        var versionDir = Plugins(host).VersionDir(Fixture, "1.0.0");
        Assert.Equal(new[] { "Acme.Dep.dll", $"{Fixture}.dll", $"{Fixture}.xml" }, Directory.GetFiles(versionDir).Select(Path.GetFileName).Order());
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(versionDir)!).Where(d => d.EndsWith(".tmp", StringComparison.Ordinal)));
        // The host's packages are never downloaded
        Assert.DoesNotContain(_net.Calls, c => c.Contains("/qodalis.cli/", StringComparison.Ordinal) || c.Contains("microsoft.extensions", StringComparison.Ordinal));
        // The plugin's details are asked for as a server plugin of this platform (the marketplace answers 404 otherwise)
        var detail = new Uri(_net.Calls.First(c => c.StartsWith($"{FakeServerMarketplaceNet.Market}/api/v1/qcli/marketplace/plugins/", StringComparison.Ordinal)));
        var detailQuery = System.Web.HttpUtility.ParseQueryString(detail.Query);
        Assert.Equal(("server", "dotnet"), (detailQuery["kind"], detailQuery["platform"]));

        var record = Plugins(host).Records().Single();
        Assert.Equal((FakeServerMarketplaceNet.Sha512Of(_net.Packages[$"{Fixture.ToLowerInvariant()}/1.0.0"]), "admin", "Fixture", "🧪"),
            (record.Integrity, record.InstalledBy, record.Title, record.Icon));
        var restart = host.Services.GetRequiredService<ICliServerRestart>();
        Assert.Contains(restart.Pending(), p => p.Id == "plugin:fixture" && p.Source == "plugins" && p.Description == "Install fixture 1.0.0");
        Assert.Contains(_audit, a => a.Event == "server.plugin.install" && a.Target == "fixture" && a.Details == $"{Fixture}@1.0.0");
        Assert.Contains(_events, e => e.Type == ServerPluginMarketplace.ChangedEvent);
        Assert.Equal(409, (await Send(client, token, HttpMethod.Post, $"{Base}/installed", new { name = "fixture" })).Status);

        // The next start loads it: needs-configuration (its apiKey), from the marketplace
        var (next, nextClient, nextToken) = await StartAsync(dir);
        Assert.NotNull(next.Services.GetRequiredService<ICliCommandRegistry>().FindProcessor("fixture-hello"));
        var (_, installed) = await Send(nextClient, nextToken, HttpMethod.Get, $"{Base}/installed");
        var view = installed!.AsArray().Single()!;
        Assert.Equal((CliServerPluginStatuses.NeedsConfiguration, "1.0.0"), (view["status"]!.GetValue<string>(), view["loadedVersion"]!.GetValue<string>()));
        Assert.Equal(["fixture.settings.apiKey"], view["missing"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.Empty(next.Services.GetRequiredService<ICliServerRestart>().Pending());
    }

    [Fact]
    public async Task ADependencyWithoutAPinnedHash_IsRefused_UnlessTheHostTrustsIt()
    {
        PublishFixture(pinDependency: false);
        var dir = TempDir();
        var (host, client, token) = await StartAsync(dir);
        var (status, body) = await Send(client, token, HttpMethod.Post, $"{Base}/installed", new { name = "fixture" });
        Assert.Equal(400, status);
        Assert.StartsWith("untrusted dependency: Acme.Dep", body!["error"]!.GetValue<string>());
        Assert.Empty(Plugins(host).Records());
        Assert.False(Directory.Exists(Path.Combine(dir, CliServerPlugins.DirName, Fixture)) &&
            Directory.EnumerateFileSystemEntries(Path.Combine(dir, CliServerPlugins.DirName, Fixture)).Any());

        var (trustedHost, trustedClient, trustedToken) = await StartAsync(TempDir(), trusted: ["Acme.*"]);
        Assert.Equal(202, (await Send(trustedClient, trustedToken, HttpMethod.Post, $"{Base}/installed", new { name = "fixture" })).Status);
        Assert.True(File.Exists(Path.Combine(Plugins(trustedHost).VersionDir(Fixture, "1.1.0"), "Acme.Dep.dll")));
        Assert.Contains(_net.Calls, c => c.EndsWith("/acme.dep/index.json", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("C:appsettings.Production.json")]
    [InlineData("..\\..\\evil.dll")]
    [InlineData("evil<1>.dll")]
    public async Task AnEntryThatIsNoPlainFileName_IsRefused_LeavingNothing(string entry)
    {
        var evil = FakeServerMarketplaceNet.Nupkg(Fixture, "1.0.0",
            new() { [$"{Fixture}.dll"] = FixtureBytes, [entry] = "{}"u8.ToArray() }, [("Qodalis.Cli", "[1.0.0-beta.1, )")]);
        _net.AddPackage(Fixture, "1.0.0", evil);
        _net.Publish("fixture", Fixture, [FakeServerMarketplaceNet.Version("1.0.0", evil)]);
        var dir = TempDir();
        var (host, client, token) = await StartAsync(dir);
        var (status, body) = await Send(client, token, HttpMethod.Post, $"{Base}/installed", new { name = "fixture" });
        Assert.Equal(502, status);
        Assert.StartsWith("invalid package: unsafe file name", body!["error"]!.GetValue<string>());
        Assert.Empty(Plugins(host).Records());
        var pluginDir = Path.GetDirectoryName(Plugins(host).VersionDir(Fixture, "1.0.0"))!;
        Assert.True(!Directory.Exists(pluginDir) || Directory.GetFileSystemEntries(pluginDir).Length == 0);
        Assert.Empty(Directory.GetFiles(dir, "*appsettings*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("Plugin.dll", true)]
    [InlineData("Plugin.xml", true)]
    [InlineData("C:appsettings.Production.json", false)]
    [InlineData("C:\\Windows\\x.dll", false)]
    [InlineData("\\\\server\\share\\x.dll", false)]
    [InlineData("..\\x.dll", false)]
    [InlineData("../x.dll", false)]
    [InlineData("a/b.dll", false)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("", false)]
    [InlineData("x.dll.", false)]
    [InlineData("x.dll ", false)]
    [InlineData("x<y>.dll", false)]
    [InlineData("nul\u0000.dll", false)]
    public void PluginFilePaths_OnlyAPlainFileNameInTheFolder(string name, bool safe)
    {
        var dir = Path.Combine(Path.GetTempPath(), "qcli-plugin-dir");
        Assert.Equal(safe, PluginFilePaths.IsSafeFileName(name));
        var resolved = PluginFilePaths.Resolve(dir, name);
        if (safe) Assert.Equal(Path.Combine(Path.GetFullPath(dir), name), resolved);
        else Assert.Null(resolved);
        if (!safe) Assert.Throws<DashboardMarketplaceException>(() => PluginFilePaths.ResolveOrThrow(dir, name, "Acme.Plugin"));
    }

    [Fact]
    public async Task IntegrityMismatches_AreRefused_StoringNothing()
    {
        PublishFixture(integrity: "sha512-" + Convert.ToBase64String(new byte[64]));
        var dir = TempDir();
        var (host, client, token) = await StartAsync(dir);
        var (status, body) = await Send(client, token, HttpMethod.Post, $"{Base}/installed", new { name = "fixture" });
        Assert.Equal((502, $"integrity mismatch: {Fixture}@1.1.0"), (status, body!["error"]!.GetValue<string>()));
        Assert.Empty(Plugins(host).Records());

        // A pinned dependency whose bytes differ
        PublishFixture();
        _net.AddPackage("Acme.Dep", "2.0.0", FakeServerMarketplaceNet.Nupkg("Acme.Dep", "2.0.0", new() { ["Acme.Dep.dll"] = [6, 6, 6] }));
        (status, body) = await Send(client, token, HttpMethod.Post, $"{Base}/installed", new { name = "fixture" });
        Assert.Equal((502, "integrity mismatch: Acme.Dep@2.0.0"), (status, body!["error"]!.GetValue<string>()));
        Assert.Empty(Plugins(host).Records());

        // No sha512 in the record at all
        _net.Publish("bare", "Acme.Bare", [FakeServerMarketplaceNet.Version("1.0.0")]);
        (status, body) = await Send(client, token, HttpMethod.Post, $"{Base}/installed", new { name = "bare" });
        Assert.Equal((502, "the marketplace record has no sha512 integrity"), (status, body!["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task APackageWithoutAnEntry_ForAnotherRuntime_OrNeedingTheHostsCode_IsRefused()
    {
        var abstractions = File.ReadAllBytes(typeof(Qodalis.Cli.Abstractions.ICliCommandProcessor).Assembly.Location);
        var noEntry = FakeServerMarketplaceNet.Nupkg("Acme.NoEntry", "1.0.0", new() { ["Acme.NoEntry.dll"] = abstractions });
        _net.AddPackage("Acme.NoEntry", "1.0.0", noEntry);
        _net.Publish("noentry", "Acme.NoEntry", [FakeServerMarketplaceNet.Version("1.0.0", noEntry)]);
        var framework = FakeServerMarketplaceNet.Nupkg("Acme.Old", "1.0.0", new() { ["Acme.Old.dll"] = abstractions }, tfm: "net48");
        _net.AddPackage("Acme.Old", "1.0.0", framework);
        _net.Publish("old", "Acme.Old", [FakeServerMarketplaceNet.Version("1.0.0", framework)]);
        var renamed = FakeServerMarketplaceNet.Nupkg("Acme.Other", "1.0.0", new() { ["Acme.Other.dll"] = FixtureBytes }, nuspecId: "Acme.Evil");
        _net.AddPackage("Acme.Other", "1.0.0", renamed);
        _net.Publish("other", "Acme.Other", [FakeServerMarketplaceNet.Version("1.0.0", renamed)]);
        PublishFixture(autoInstall: false);
        _net.Publish("weather", "Qodalis.Cli.Plugin.Weather", [FakeServerMarketplaceNet.Version("1.0.0", [1])]);
        var (host, client, token) = await StartAsync(TempDir());

        async Task<(int, string)> Install(string name)
        {
            var (s, b) = await Send(client, token, HttpMethod.Post, $"{Base}/installed", new { name });
            return (s, b!["error"]!.GetValue<string>());
        }

        Assert.Equal((400, "Acme.NoEntry declares no [assembly: CliPlugin(typeof(...))] entry"), await Install("noentry"));
        Assert.Equal(400, (await Install("old")).Item1);
        Assert.Contains("has no assemblies for .NET", (await Install("old")).Item2);
        Assert.Equal((502, "the package is Acme.Evil@1.0.0, not Acme.Other@1.0.0"), await Install("other"));
        Assert.Equal((400, "needs setup in code: needs the host's database"), await Install("fixture"));
        Assert.Equal((409, "already installed by the host: Qodalis.Cli.Plugin.Weather"), await Install("weather"));
        Assert.Equal((404, "plugin not found: nope"), await Install("nope"));
        Assert.Empty(Plugins(host).Records());
    }

    [Fact]
    public async Task TheServerRange_OfTheRecord_IsChecked()
    {
        PublishFixture(range: ">=99.0.0");
        var (_, client, token) = await StartAsync(TempDir());
        var (status, body) = await Send(client, token, HttpMethod.Post, $"{Base}/installed", new { name = "fixture", version = "1.0.0" });
        Assert.Equal(400, status);
        Assert.StartsWith($"incompatible: {Fixture}@1.0.0 needs server >=99.0.0", body!["error"]!.GetValue<string>());
        var (_, details) = await Send(client, token, HttpMethod.Get, $"{Base}/plugins/fixture");
        Assert.False(details!["allowed"]!.GetValue<bool>());
        Assert.StartsWith("needs server >=99.0.0", details["blockedReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Update_AndRemove_MarkAndClearTheRestart()
    {
        PublishFixture();
        var dir = TempDir();
        var (host, client, token) = await StartAsync(dir);
        await Send(client, token, HttpMethod.Post, $"{Base}/installed", new { name = "fixture", version = "1.0.0" });

        var (status, body) = await Send(client, token, HttpMethod.Put, $"{Base}/installed/fixture", new { });
        Assert.Equal(202, status);
        Assert.Equal(("1.1.0", "1.0.0", true), (body!["version"]!.GetValue<string>(), body["previousVersion"]!.GetValue<string>(), body["changed"]!.GetValue<bool>()));
        Assert.False(Directory.Exists(Plugins(host).VersionDir(Fixture, "1.0.0")));
        Assert.True(Directory.Exists(Plugins(host).VersionDir(Fixture, "1.1.0")));
        (status, body) = await Send(client, token, HttpMethod.Put, $"{Base}/installed/fixture", new { version = "1.1.0" });
        Assert.Equal((200, false), (status, body!["changed"]!.GetValue<bool>()));
        Assert.Contains(_audit, a => a.Event == "server.plugin.update" && a.Details == $"{Fixture} 1.0.0 -> 1.1.0");

        // Removed before it ever ran: nothing waits for a restart
        (status, body) = await Send(client, token, HttpMethod.Delete, $"{Base}/installed/fixture");
        Assert.Equal((202, false), (status, body!["restartRequired"]!.GetValue<bool>()));
        Assert.Empty(Plugins(host).Records());
        Assert.Empty(host.Services.GetRequiredService<ICliServerRestart>().Pending());
        Assert.False(Directory.Exists(Path.Combine(dir, CliServerPlugins.DirName, Fixture)));
        Assert.Equal(404, (await Send(client, token, HttpMethod.Delete, $"{Base}/installed/fixture")).Status);
    }

    [Fact]
    public async Task RemovingARunningPlugin_KeepsItListedAsRemoved_UntilARestart()
    {
        PublishFixture();
        var dir = TempDir();
        var (_, client, token) = await StartAsync(dir);
        await Send(client, token, HttpMethod.Post, $"{Base}/installed", new { name = "fixture" });
        var (running, runningClient, runningToken) = await StartAsync(dir);

        var (status, body) = await Send(runningClient, runningToken, HttpMethod.Delete, $"{Base}/installed/fixture");
        Assert.Equal((202, "removed", true), (status, body!["status"]!.GetValue<string>(), body["restartRequired"]!.GetValue<bool>()));
        var (_, installed) = await Send(runningClient, runningToken, HttpMethod.Get, $"{Base}/installed");
        Assert.Equal("removed", installed!.AsArray().Single()!["status"]!.GetValue<string>());
        Assert.Contains(running.Services.GetRequiredService<ICliServerRestart>().Pending(), p => p.Id == "plugin:fixture");
    }

    [Fact]
    public async Task PluginsAddedInCode_AreNotListedAsInstalled_AndPoliciesApply()
    {
        PublishFixture();
        var (_, client, token) = await StartAsync(TempDir(), install: "off");
        var (status, body) = await Send(client, token, HttpMethod.Post, $"{Base}/installed", new { name = "fixture" });
        Assert.Equal((403, "install disabled"), (status, body!["error"]!.GetValue<string>()));
        Assert.Equal(400, (await Send(client, token, HttpMethod.Post, $"{Base}/installed", new { name = "../x" })).Status);
        Assert.Equal(400, (await Send(client, token, HttpMethod.Post, $"{Base}/installed", new { name = "fixture", version = "nope" })).Status);
        var (_, installed) = await Send(client, token, HttpMethod.Get, $"{Base}/installed");
        Assert.Empty(installed!.AsArray());

        var (_, offClient, offToken) = await StartAsync(TempDir(), enabled: false);
        Assert.Equal(404, (await Send(offClient, offToken, HttpMethod.Get, Base)).Status);
    }

    [Fact]
    public async Task ThePermissions_AdminAccessToRead_PluginsInstallToChange()
    {
        PublishFixture();
        var dir = TempDir();
        var host = await Helpers.UsersTestHost.StartAsync(o => o.MinPasswordLength = 4,
            configureCli: cli =>
            {
                cli.ConfigureServerPlugins(o => o.DataDir = dir);
                cli.AddAdmin(o => o.Marketplace = new DashboardMarketplaceOptions
                {
                    Url = FakeServerMarketplaceNet.Market, NuGetFeed = FakeServerMarketplaceNet.Feed, HttpHandler = _net,
                    Dir = Path.Combine(dir, "dashboard-plugins"),
                });
            },
            configureApp: app => app.UseQodalisAdmin());
        try
        {
            var root = await host.RootTokenAsync();
            var viewer = await host.UserWithPermissionsAsync(root, "viewer", ["admin:access"]);
            var installer = await host.UserWithPermissionsAsync(root, "installer", ["admin:access", "plugins:install"]);
            var outsider = await host.UserWithPermissionsAsync(root, "outsider", ["commands:execute"]);

            Assert.Equal(403, (int)(await host.GetAsync(Base, outsider)).StatusCode);
            Assert.Equal(200, (int)(await host.GetAsync(Base, viewer)).StatusCode);
            var denied = await host.SendAsync(HttpMethod.Post, $"{Base}/installed", viewer, new { name = "fixture" });
            Assert.Equal(403, (int)denied.StatusCode);
            Assert.Contains("plugins:install", await denied.Content.ReadAsStringAsync());
            Assert.Equal(202, (int)(await host.SendAsync(HttpMethod.Post, $"{Base}/installed", installer, new { name = "fixture" })).StatusCode);
            Assert.Equal(403, (int)(await host.SendAsync(HttpMethod.Delete, $"{Base}/installed/fixture", viewer)).StatusCode);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------- the admin command

    [Fact]
    public async Task AdminPlugins_SearchInstalledInstallUpdateRemove()
    {
        PublishFixture();
        var (host, client, token) = await StartAsync(TempDir());
        async Task<string> Run(string line) => (await ConfigCommands.RunAsync(client, host.Services.GetRequiredService<ICliCommandRegistry>(), line, token)).Text;

        var search = await Run("admin plugins search fixture");
        Assert.Contains("Plugin", search);
        Assert.Contains(Fixture, search);
        Assert.Contains("dotnet, node", search);
        Assert.Contains("Qodalis (verified)", search);
        Assert.Contains("installable", search);
        Assert.StartsWith("No server plugins installed from the marketplace", await Run("admin plugins installed"));

        Assert.Equal($"Installed fixture: {Fixture}@1.0.0.\nRestart the server to apply it: admin restart (or the dashboard).",
            await Run("admin plugins install fixture@1.0.0"));
        var installed = await Run("admin plugins installed");
        Assert.Contains("1 server plugin(s) from https://market.test (install: verified)", installed);
        Assert.Contains("1.0.0 (update available)", installed);
        Assert.Contains("pending restart", installed);
        Assert.Equal("Updated fixture from 1.0.0 to 1.1.0.\nRestart the server to apply it: admin restart (or the dashboard).",
            await Run("admin plugins update fixture"));
        Assert.Equal("fixture is already at 1.1.0.", await Run("admin plugins update fixture"));
        Assert.Equal($"Removed fixture ({Fixture}@1.1.0).\nNo restart needed: the server already runs this.", await Run("admin plugins remove fixture"));
        Assert.Equal("Error: not installed: fixture", await Run("admin plugins remove fixture"));
        Assert.Equal("Error: name one plugin: admin plugins install <name>[@version]", await Run("admin plugins install a b"));
    }
}
