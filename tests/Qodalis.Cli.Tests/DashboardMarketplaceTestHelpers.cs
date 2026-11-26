using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Config;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>A fake version of a marketplace plugin: what it publishes and what the CDNs serve.</summary>
internal sealed record FakeVersion(string Version, string Status = "listed", int DashboardApi = 1, string? Js = null, string? Css = "",
    string? Jsdelivr = "", string? Unpkg = "", JsonNode? Config = null)
{
    // "" = the published bytes, null = nothing (404), other text = those bytes instead; Config: the version's settings
    // (`config` of its record), null: the record has none
}

/// <summary>The marketplace's public API and the two CDNs, in memory (an <see cref="HttpMessageHandler"/>).</summary>
internal sealed class FakeMarketplaceNet : HttpMessageHandler
{
    public const string Market = "https://market.test";
    public const string Jsdelivr = "https://cdn.jsdelivr.net/npm";
    public const string Unpkg = "https://unpkg.com";

    public readonly Dictionary<string, JsonObject> Plugins = new();
    public readonly ConcurrentDictionary<string, byte[]> Files = new();
    public readonly ConcurrentQueue<string> Calls = new();
    public bool MarketplaceDown;
    /// <summary>Answer details without <c>files</c> (an older marketplace): the version record has them.</summary>
    public bool FilesOnlyInVersionRecord;
    /// <summary>Answer details without the versions' <c>config</c>: the version record has it.</summary>
    public bool ConfigOnlyInVersionRecord;
    /// <summary>Ignore the <c>kind</c> filter (the marketplace before dashboard plugins): every plugin listed and counted.</summary>
    public bool IgnoresKind;
    /// <summary>Answers every marketplace request with this status instead.</summary>
    public HttpStatusCode? MarketplaceStatus;

    public static string IntegrityOf(byte[] bytes) => $"sha384-{Convert.ToBase64String(SHA384.HashData(bytes))}";

    public List<string> CdnCalls() => Calls.Where(c => !c.StartsWith(Market, StringComparison.Ordinal)).ToList();

    public void Publish(string name, IEnumerable<FakeVersion> versions, bool verified = true, string kind = "dashboard", string? title = null,
        string? icon = null, JsonNode? serverPackages = null, string? extensionId = null)
    {
        var records = new JsonArray();
        foreach (var v in versions)
        {
            var js = Encoding.UTF8.GetBytes(v.Js ?? $"export default {{ id: '{name}', version: '{v.Version}' }};");
            var css = v.Css == null ? null : Encoding.UTF8.GetBytes(v.Css.Length > 0 ? v.Css : $".x-{Regex.Replace(v.Version, @"\W", "")} {{ color: red; }}");
            var files = new JsonArray { new JsonObject { ["path"] = "dist/extension.js", ["integrity"] = IntegrityOf(js), ["size"] = js.Length } };
            if (css != null) files.Add(new JsonObject { ["path"] = "dist/extension.css", ["integrity"] = IntegrityOf(css), ["size"] = css.Length });
            void Put(string cdn, string path, byte[] published, string? served)
            {
                if (served == null) return;
                Files[$"{cdn}/{name}@{v.Version}/{path}"] = served.Length == 0 ? published : Encoding.UTF8.GetBytes(served);
            }
            Put(Jsdelivr, "dist/extension.js", js, v.Jsdelivr);
            Put(Unpkg, "dist/extension.js", js, v.Unpkg);
            if (css != null)
            {
                Put(Jsdelivr, "dist/extension.css", css, v.Jsdelivr == null ? null : "");
                Put(Unpkg, "dist/extension.css", css, "");
            }
            var record = new JsonObject
            {
                ["version"] = v.Version,
                ["status"] = v.Status,
                ["kind"] = kind,
                ["files"] = files,
                ["dashboardApi"] = v.DashboardApi,
                ["publishedAt"] = "2026-11-20T10:00:00.000Z",
            };
            if (serverPackages != null) record["serverPackages"] = serverPackages.DeepClone();
            if (v.Config != null) record["config"] = v.Config.DeepClone();
            records.Add(record);
        }
        var listed = records.Select(r => r!["version"]!.GetValue<string>())
            .Where(v => records.First(r => r!["version"]!.GetValue<string>() == v)!["status"]!.GetValue<string>() == "listed")
            .OrderBy(v => v, Comparer<string>.Create(DashboardMarketplace.CompareVersions)).ToList();
        var latest = listed.LastOrDefault();
        var scope = name.StartsWith('@') ? name[..name.IndexOf('/')] : name;
        var plugin = new JsonObject
        {
            ["name"] = name,
            ["shortName"] = name.Split('/')[^1],
            ["description"] = $"The {name} page",
            ["kind"] = kind,
        };
        if (title != null) plugin["title"] = title;
        if (icon != null) plugin["icon"] = icon;
        plugin["publisher"] = new JsonObject { ["id"] = scope.TrimStart('@'), ["name"] = scope, ["verified"] = verified };
        plugin["latest"] = latest;
        plugin["version"] = latest;
        plugin["dashboardApi"] = records.FirstOrDefault(r => r!["version"]!.GetValue<string>() == latest)?["dashboardApi"]?.GetValue<int>() ?? 1;
        if (serverPackages != null) plugin["serverPackages"] = serverPackages.DeepClone();
        if (extensionId != null) plugin["extension"] = new JsonObject { ["id"] = extensionId };
        if (records.FirstOrDefault(r => r!["version"]!.GetValue<string>() == latest)?["config"] is { } latestConfig)
            plugin["config"] = latestConfig.DeepClone();
        plugin["versions"] = records;
        lock (Plugins) Plugins[name] = plugin;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Calls.Enqueue(url);
        if (url.StartsWith(Market, StringComparison.Ordinal))
        {
            if (MarketplaceDown) throw new HttpRequestException("fetch failed");
            if (MarketplaceStatus is { } status) return Task.FromResult(Json(status, new JsonObject { ["error"] = "boom" }));
            lock (Plugins) return Task.FromResult(Marketplace(request.RequestUri));
        }
        return Task.FromResult(Files.TryGetValue(url, out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "not found" }));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private HttpResponseMessage Marketplace(Uri uri)
    {
        var path = uri.AbsolutePath["/api/v1/qcli/marketplace".Length..];
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (path == "/plugins")
        {
            var text = (query["query"] ?? "").ToLowerInvariant();
            var kind = query["kind"];
            var items = new JsonArray();
            foreach (var p in Plugins.Values.Where(p => (kind == null || IgnoresKind || p["kind"]!.GetValue<string>() == kind)
                                                        && p["name"]!.GetValue<string>().Contains(text)))
            {
                var summary = p.DeepClone().AsObject();
                summary.Remove("versions");
                summary.Remove("extension");
                items.Add(summary);
            }
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["items"] = items,
                ["total"] = items.Count,
                ["page"] = int.Parse(query["page"] ?? "1"),
                ["pageSize"] = int.Parse(query["pageSize"] ?? "24"),
            });
        }
        var m = Regex.Match(path, "^/plugins/(.+?)(?:/versions/([^/]+))?$");
        if (!m.Success) return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "not found" });
        if (!Plugins.TryGetValue(Uri.UnescapeDataString(m.Groups[1].Value), out var plugin))
            return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "plugin not found" });
        var versions = plugin["versions"]!.AsArray();
        if (m.Groups[2].Success)
        {
            var v = versions.FirstOrDefault(x => x!["version"]!.GetValue<string>() == Uri.UnescapeDataString(m.Groups[2].Value));
            return v != null ? Json(HttpStatusCode.OK, v.DeepClone()) : Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "version not found" });
        }
        var copy = plugin.DeepClone().AsObject();
        if (FilesOnlyInVersionRecord)
            foreach (var v in copy["versions"]!.AsArray()) v!.AsObject().Remove("files");
        if (ConfigOnlyInVersionRecord)
            foreach (var v in copy["versions"]!.AsArray()) v!.AsObject().Remove("config");
        return Json(HttpStatusCode.OK, copy);
    }
}

/// <summary>An audit store keeping the entries in a list.</summary>
internal sealed class MarketplaceAuditList : IAuditStore
{
    public List<AuditEntry> Entries { get; } = [];

    public Task LogAsync(AuditEntry entry)
    {
        lock (Entries) Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<List<AuditEntry>> GetEntriesAsync(AuditQueryOptions? options = null) => Task.FromResult(Entries.ToList());

    public Task ClearAsync()
    {
        Entries.Clear();
        return Task.CompletedTask;
    }
}

/// <summary>A server with the admin plugin (password sign-in) and the fake marketplace.</summary>
internal sealed class MarketplaceTestHost(IHost host, string dir, FakeMarketplaceNet net, MarketplaceAuditList audit) : IAsyncDisposable
{
    public const string Base = "/api/v1/qcli/dashboard/marketplace";

    public IHost Server { get; } = host;
    public HttpClient Client { get; } = host.GetTestClient();
    public string Dir { get; } = dir;
    public FakeMarketplaceNet Net { get; } = net;
    public MarketplaceAuditList Audit { get; } = audit;

    /// <summary>The <c>dashboard:*</c> events.</summary>
    public List<CliEvent> Events { get; } = [];

    /// <summary>The <c>config:*</c> events.</summary>
    public List<CliEvent> ConfigEvents { get; } = [];

    public string Token { get; set; } = "";

    public T Service<T>() where T : notnull => Server.Services.GetRequiredService<T>();

    public Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, bool auth = true, string? token = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (auth) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Token);
        if (body != null) req.Content = body is JsonNode node ? new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") : JsonContent.Create(body);
        return Client.SendAsync(req);
    }

    public Task<HttpResponseMessage> Get(string path, bool auth = true) => Send(HttpMethod.Get, path, auth: auth);
    public Task<HttpResponseMessage> Post(object body) => Send(HttpMethod.Post, $"{Base}/installed", body);
    public Task<HttpResponseMessage> Put(string name, object? body = null) => Send(HttpMethod.Put, $"{Base}/installed/{name}", body ?? new { });
    public Task<HttpResponseMessage> Delete(string name) => Send(HttpMethod.Delete, $"{Base}/installed/{name}");

    /// <summary>Runs a command as the static admin (the dashboard's sign-in).</summary>
    public async Task<string> Run(string line) =>
        (await ConfigCommands.RunAsync(Client, Service<ICliCommandRegistry>(), line, Token.Length > 0 ? Token : null)).Text;

    /// <summary>Runs a command without signing in.</summary>
    public Task<(int Status, string Text, System.Text.Json.JsonElement Body)> RunAnonymous(string line) =>
        ConfigCommands.RunAsync(Client, Service<ICliCommandRegistry>(), line);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Server.StopAsync();
        Server.Dispose();
    }
}

/// <summary>The marketplace test servers of a test class, and their temporary directories, disposed with it.</summary>
internal sealed class MarketplaceTestServers : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _hosts = [];
    private readonly List<string> _dirs = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts) await host.DisposeAsync();
        foreach (var dir in _dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    public string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qcli-dashboard-plugins-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    /// <summary>A server with the admin plugin (password sign-in as admin) and the fake marketplace in <paramref name="dir"/>.</summary>
    public async Task<MarketplaceTestHost> StartAsync(FakeMarketplaceNet? net = null, string? dir = null, string? install = null,
        Action<CliBuilder>? configure = null, bool enabled = true, string? contentRoot = null, bool setDir = true)
    {
        net ??= new FakeMarketplaceNet();
        dir ??= TempDir();
        var audit = new MarketplaceAuditList();
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            if (contentRoot != null) web.UseContentRoot(contentRoot);
            web.ConfigureServices(s => s.AddControllers().AddCli(cli =>
            {
                cli.Services.AddSingleton<IAuditStore>(audit);
                configure?.Invoke(cli);
                cli.AddAdmin(o =>
                {
                    o.Username = "admin";
                    o.Password = "pass";
                    o.JwtSecret = "dashboard-marketplace-test-secret-long-enough-for-hmac";
                    o.Marketplace = new DashboardMarketplaceOptions
                    {
                        Enabled = enabled,
                        Url = FakeMarketplaceNet.Market,
                        Dir = setDir ? dir : null,
                        HttpHandler = net,
                        Install = install ?? "verified",
                    };
                });
            }));
            web.Configure(app =>
            {
                app.UseWebSockets();
                app.UseRouting();
                app.UseCors();
                app.UseQodalisAdmin();
                app.UseEndpoints(e => e.MapControllers());
                app.UseCli();
            });
        });
        var host = new MarketplaceTestHost(await builder.StartAsync(), dir, net, audit);
        _hosts.Add(host);
        var bus = host.Service<ICliEventBus>();
        bus.Subscribe("dashboard:*", (e, _) =>
        {
            lock (host.Events) host.Events.Add(e);
            return Task.CompletedTask;
        });
        bus.Subscribe("config:*", (e, _) =>
        {
            lock (host.ConfigEvents) host.ConfigEvents.Add(e);
            return Task.CompletedTask;
        });
        var login = await host.Client.PostAsJsonAsync("/api/v1/qcli/auth/login", new { username = "admin", password = "pass" });
        host.Token = JsonNode.Parse(await login.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();
        return host;
    }

    /// <summary>A protected server (users plugin) whose dashboard signs in through it; one user per entry with those permissions.</summary>
    public async Task<(UsersTestHost Host, FakeMarketplaceNet Net, string Dir, Dictionary<string, string> Tokens)> StartProtectedAsync(
        Dictionary<string, string[]> users)
    {
        var net = new FakeMarketplaceNet();
        var dir = TempDir();
        var host = await UsersTestHost.StartAsync(o => o.MinPasswordLength = 4,
            configureCli: cli => cli.AddAdmin(o => o.Marketplace = new DashboardMarketplaceOptions { Url = FakeMarketplaceNet.Market, Dir = dir, HttpHandler = net }),
            configureApp: app => app.UseQodalisAdmin());
        _hosts.Add(host);
        var tokens = new Dictionary<string, string> { ["root"] = await host.RootTokenAsync() };
        foreach (var (name, permissions) in users) tokens[name] = await host.UserWithPermissionsAsync(tokens["root"], name, permissions);
        return (host, net, dir, tokens);
    }
}
