using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Abstractions.Notifications;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Notifications;
using Qodalis.Cli.Plugins;
using Qodalis.Cli.Restart;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>A plugin entry that fails to load.</summary>
public sealed class NotifBrokenEntry : ICliPluginEntry
{
    public void Register(CliPluginContext context) => throw new InvalidOperationException("cannot connect");
}

/// <summary>A plugin entry that needs a setting it does not have.</summary>
public sealed class NotifNeedyEntry : ICliPluginEntry
{
    public void Register(CliPluginContext context) => context.NeedsConfiguration("settings.apiKey");
}

/// <summary>A plugin entry that loads.</summary>
public sealed class NotifFineEntry : ICliPluginEntry
{
    public void Register(CliPluginContext context)
    {
    }
}

/// <summary>A plugin entry that publishes a notification of its own while it registers.</summary>
public sealed class NotifTalkerEntry : ICliPluginEntry
{
    public void Register(CliPluginContext context) =>
        context.Notifications.Notify(new CliNotificationInput { Key = "talker:hello", Kind = "talker", Severity = "info", Title = "Hello from a plugin" });
}

/// <summary>
/// System notifications end to end: the API (<c>/api/v1/qcli/notifications</c>), permission filtering and per-user read
/// state on a protected server, <c>notification:changed</c> on the events socket (with each socket's own unread count),
/// the <c>notifications</c> command, the <c>notifications_list</c> MCP tool, persistence across a restart and the
/// server's own sources (security warning, restart required, plugins that failed or need configuration).
/// </summary>
public sealed class NotificationsApiTests : IAsyncLifetime
{
    private const string Url = "/api/v1/qcli/notifications";

    private readonly List<IAsyncDisposable> _hosts = [];
    private readonly List<string> _dirs = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts) await host.DisposeAsync();
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
        var dir = Path.Combine(Path.GetTempPath(), $"qcli-notif-api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private sealed class Host(IHost host) : IAsyncDisposable
    {
        public IHost Server { get; } = host;
        public HttpClient Client { get; } = host.GetTestClient();
        public T Service<T>() where T : notnull => Server.Services.GetRequiredService<T>();
        public CliNotificationService Notifications => Service<CliNotificationService>();
        private bool _disposed;

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            Client.Dispose();
            await Server.StopAsync();
            Server.Dispose();
        }
    }

    /// <summary>An open server (no sign-in) keeping its data in <paramref name="dataDir"/> (a new folder when null).</summary>
    private async Task<Host> StartAsync(string? dataDir = null, Action<CliBuilder>? configure = null)
    {
        dataDir ??= TempDir();
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(s => s.AddControllers().AddCli(cli =>
            {
                cli.ConfigureServerPlugins(o => o.DataDir = dataDir);
                cli.ConfigureRestart(o => o.Runtime = new FakeRestartRuntime());
                configure?.Invoke(cli);
            }));
            web.Configure(app =>
            {
                app.UseWebSockets();
                app.UseRouting();
                app.UseEndpoints(e => e.MapControllers());
                app.UseCli();
            });
        });
        var host = new Host(await builder.StartAsync());
        _hosts.Add(host);
        // The plugins' notifications are synced by the settings check after start
        await host.Service<CliServerPlugins>().StartupCheck.WaitAsync(TimeSpan.FromSeconds(10));
        return host;
    }

    /// <summary>A protected server (users plugin) with real event sockets; one user per entry with those permissions.</summary>
    private async Task<(UsersTestHost Host, Dictionary<string, string> Tokens)> StartProtectedAsync(Dictionary<string, string[]> users)
    {
        var dataDir = TempDir();
        var host = await UsersTestHost.StartAsync(o => o.MinPasswordLength = 4,
            configureCli: cli => cli.ConfigureServerPlugins(o => o.DataDir = dataDir), realEventSockets: true);
        _hosts.Add(host);
        var tokens = new Dictionary<string, string> { ["root"] = await host.RootTokenAsync() };
        foreach (var (name, permissions) in users) tokens[name] = await host.UserWithPermissionsAsync(tokens["root"], name, permissions);
        return (host, tokens);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string? token = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(req);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res, int status = 200)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(status == (int)res.StatusCode, $"expected {status}, got {(int)res.StatusCode}: {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static List<string> Keys(JsonElement list) =>
        list.GetProperty("items").EnumerateArray().Select(n => n.GetProperty("key").GetString()!).Order().ToList();

    private static CliNotificationInput Note(string key, string severity = "info", string? title = null, string? permission = null, string? link = null) =>
        NotificationServiceTests.Input(key, severity, title ?? $"About {key}", permission: permission, link: link);

    // ---------------------------------------------------------------- open server

    [Fact]
    public async Task OpenServer_ListsReadsReadsAllDismissesAndClears()
    {
        var h = await StartAsync();
        var a = h.Notifications.Notify(Note("a"));
        h.Notifications.Notify(Note("b", "error", link: "/plugins"));

        var res = await Send(h.Client, HttpMethod.Get, Url);
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
        var list = await Json(res);
        Assert.Equal(2, list.GetProperty("unreadCount").GetInt32());
        Assert.Equal(["a", "b"], Keys(list));
        var b = list.GetProperty("items").EnumerateArray().Single(n => n.GetProperty("key").GetString() == "b");
        Assert.Equal(["id", "key", "kind", "severity", "title", "message", "link", "createdAt", "updatedAt", "read"],
            b.EnumerateObject().Select(p => p.Name));
        Assert.False(b.GetProperty("read").GetBoolean());
        Assert.Equal(["b"], Keys(await Json(await Send(h.Client, HttpMethod.Get, $"{Url}?severity=error"))));

        var read = await Json(await Send(h.Client, HttpMethod.Post, $"{Url}/{a.Id}/read"));
        Assert.Equal((a.Id, true, 1), (read.GetProperty("item").GetProperty("id").GetString(), read.GetProperty("item").GetProperty("read").GetBoolean(),
            read.GetProperty("unreadCount").GetInt32()));
        Assert.Equal(["b"], Keys(await Json(await Send(h.Client, HttpMethod.Get, $"{Url}?unread=true"))));
        Assert.Equal(["b"], Keys(await Json(await Send(h.Client, HttpMethod.Get, $"{Url}?unread=1"))));

        Assert.Equal("{\"marked\":1,\"unreadCount\":0}", (await Json(await Send(h.Client, HttpMethod.Post, $"{Url}/read-all"))).GetRawText());

        Assert.Equal("{\"dismissed\":true,\"unreadCount\":0}", (await Json(await Send(h.Client, HttpMethod.Delete, $"{Url}/{a.Id}"))).GetRawText());
        var bId = h.Notifications.Get("b")!.Id;
        Assert.Equal("{\"cleared\":true,\"unreadCount\":0}", (await Json(await Send(h.Client, HttpMethod.Delete, $"{Url}/{bId}?scope=all"))).GetRawText());
        Assert.Equal("{\"items\":[],\"unreadCount\":0}", (await Json(await Send(h.Client, HttpMethod.Get, Url))).GetRawText());
        Assert.Null(h.Notifications.Get("b"));
    }

    [Fact]
    public async Task OpenServer_404ForAnUnknownId_400ForABadFilter()
    {
        var h = await StartAsync();
        Assert.Equal("notification not found", (await Json(await Send(h.Client, HttpMethod.Post, $"{Url}/n_nope/read"), 404)).GetProperty("error").GetString());
        await Json(await Send(h.Client, HttpMethod.Delete, $"{Url}/n_nope"), 404);
        await Json(await Send(h.Client, HttpMethod.Delete, $"{Url}/n_nope?scope=all"), 404);
        await Json(await Send(h.Client, HttpMethod.Get, $"{Url}?severity=fatal"), 400);
        await Json(await Send(h.Client, HttpMethod.Get, $"{Url}?limit=0"), 400);
        await Json(await Send(h.Client, HttpMethod.Get, $"{Url}?limit=501"), 400);
        await Json(await Send(h.Client, HttpMethod.Get, $"{Url}?limit=x"), 400);
        h.Notifications.Notify(Note("one"));
        h.Notifications.Notify(Note("two"));
        Assert.Single((await Json(await Send(h.Client, HttpMethod.Get, $"{Url}?limit=1"))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task PersistsAcrossARestart_SameDataDirectory_ReadStateIncluded()
    {
        var dataDir = TempDir();
        var first = await StartAsync(dataDir);
        var n = first.Notifications.Notify(Note("kept"));
        await Json(await Send(first.Client, HttpMethod.Post, $"{Url}/{n.Id}/read"));
        await first.DisposeAsync();
        Assert.True(File.Exists(Path.Combine(dataDir, CliNotificationsOptions.FileName)));

        var second = await StartAsync(dataDir);
        var list = await Json(await Send(second.Client, HttpMethod.Get, Url));
        Assert.Equal(0, list.GetProperty("unreadCount").GetInt32());
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal((n.Id, "kept", true), (item.GetProperty("id").GetString(), item.GetProperty("key").GetString(), item.GetProperty("read").GetBoolean()));
    }

    [Fact]
    public async Task MemoryOnly_WithPersistFalse()
    {
        var dataDir = TempDir();
        var first = await StartAsync(dataDir, cli => cli.ConfigureNotifications(o => o.Persist = false));
        first.Notifications.Notify(Note("gone"));
        await first.DisposeAsync();
        Assert.False(File.Exists(Path.Combine(dataDir, CliNotificationsOptions.FileName)));
        var second = await StartAsync(dataDir);
        Assert.Empty((await Json(await Send(second.Client, HttpMethod.Get, Url))).GetProperty("items").EnumerateArray());
    }

    // ---------------------------------------------------------------- protected server

    [Fact]
    public async Task ProtectedServer_FiltersByPermission_KeepsReadStatePerUser_ClearForEveryoneNeedsManage()
    {
        var (h, tokens) = await StartProtectedAsync(new()
        {
            ["admin"] = ["admin:access"], ["plain"] = ["commands:execute"], ["manager"] = ["notifications:manage"],
        });
        var notifications = h.Service<CliNotificationService>();
        var adminOnly = notifications.Notify(Note("admin-only", permission: "admin:access"));
        var everyone = notifications.Notify(Note("everyone"));

        Assert.Equal(401, (int)(await Send(h.Client, HttpMethod.Get, Url)).StatusCode);
        async Task<List<string>> KeysOf(string who) => Keys(await Json(await Send(h.Client, HttpMethod.Get, Url, tokens[who])));
        Assert.Equal(["admin-only", "everyone"], await KeysOf("admin"));
        Assert.Equal(["everyone"], await KeysOf("plain"));
        Assert.Equal(["admin-only", "everyone"], await KeysOf("root"));

        // a hidden one is not found
        await Json(await Send(h.Client, HttpMethod.Post, $"{Url}/{adminOnly.Id}/read", tokens["plain"]), 404);
        await Json(await Send(h.Client, HttpMethod.Delete, $"{Url}/{adminOnly.Id}", tokens["plain"]), 404);

        await Json(await Send(h.Client, HttpMethod.Post, $"{Url}/{everyone.Id}/read", tokens["admin"]));
        Assert.Equal(1, (await Json(await Send(h.Client, HttpMethod.Get, Url, tokens["admin"]))).GetProperty("unreadCount").GetInt32());
        Assert.Equal(1, (await Json(await Send(h.Client, HttpMethod.Get, Url, tokens["plain"]))).GetProperty("unreadCount").GetInt32());

        // dismiss: for the caller only
        await Json(await Send(h.Client, HttpMethod.Delete, $"{Url}/{everyone.Id}", tokens["plain"]));
        Assert.Empty(await KeysOf("plain"));
        Assert.Equal(["admin-only", "everyone"], await KeysOf("admin"));

        // for everyone: notifications:manage
        var refused = await Json(await Send(h.Client, HttpMethod.Delete, $"{Url}/{everyone.Id}?scope=all", tokens["admin"]), 403);
        Assert.Equal("forbidden: missing permission notifications:manage", refused.GetProperty("error").GetString());
        await Json(await Send(h.Client, HttpMethod.Delete, $"{Url}/{everyone.Id}?scope=all", tokens["manager"]));
        Assert.Equal(["admin-only"], await KeysOf("admin"));
    }

    [Fact]
    public async Task ListsTheNewPermissionsInTheCatalog_GroupServer_RightAfterServerRestart()
    {
        var h = await StartAsync();
        var catalog = h.Service<Qodalis.Cli.Security.ICliPermissionCatalog>();
        Assert.Equal(("Server", "cli"), (catalog.Find("notifications:manage")!.Group, catalog.Find("notifications:manage")!.Surface));
        Assert.Equal("See the available updates and check for them", catalog.Find("updates:read")!.Description);
        var names = catalog.All.Select(p => p.Name).ToList();
        Assert.Equal(["server:restart", "updates:read", "notifications:manage"], names.Skip(names.IndexOf("server:restart")).Take(3));
    }

    // ---------------------------------------------------------------- events socket

    private static async Task<JsonElement> NextAsync(WebSocket socket, Func<JsonElement, bool> match)
    {
        while (true)
        {
            var message = JsonSerializer.Deserialize<JsonElement>(await UsersTestHost.ReceiveTextAsync(socket));
            if (message.GetProperty("type").GetString() == CliNotificationConstants.ChangedEvent && match(message)) return message;
        }
    }

    /// <summary>The <c>notification:changed</c> messages a socket got before the marker the test broadcasts.</summary>
    private static async Task<List<JsonElement>> ChangesUntilMarkerAsync(WebSocket socket)
    {
        var seen = new List<JsonElement>();
        while (true)
        {
            var message = JsonSerializer.Deserialize<JsonElement>(await UsersTestHost.ReceiveTextAsync(socket));
            var type = message.GetProperty("type").GetString();
            if (type == "marker") return seen;
            if (type == CliNotificationConstants.ChangedEvent) seen.Add(message);
        }
    }

    [Fact]
    public async Task EventsSocket_OpenServer_CarriesTheActionTheItemAndTheUnreadCount()
    {
        var h = await StartAsync();
        var socket = await h.Server.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws/v1/qcli/events"), CancellationToken.None);
        Assert.Contains("connected", await UsersTestHost.ReceiveTextAsync(socket));

        var n = h.Notifications.Notify(Note("ws", "warning"));
        var created = await NextAsync(socket, _ => true);
        Assert.Equal(("created", n.Id, "ws", 1), (created.GetProperty("action").GetString(), created.GetProperty("id").GetString(),
            created.GetProperty("key").GetString(), created.GetProperty("unreadCount").GetInt32()));
        var item = created.GetProperty("item");
        Assert.Equal((n.Id, "About ws", "warning", false), (item.GetProperty("id").GetString(), item.GetProperty("title").GetString(),
            item.GetProperty("severity").GetString(), item.GetProperty("read").GetBoolean()));
        Assert.True(created.TryGetProperty("timestamp", out _));

        await Json(await Send(h.Client, HttpMethod.Post, $"{Url}/{n.Id}/read"));
        var read = await NextAsync(socket, m => m.GetProperty("action").GetString() == "read");
        Assert.Equal((0, true), (read.GetProperty("unreadCount").GetInt32(), read.GetProperty("item").GetProperty("read").GetBoolean()));

        h.Notifications.Resolve("ws");
        var resolved = await NextAsync(socket, m => m.GetProperty("action").GetString() == "resolved");
        Assert.Equal(("ws", 0), (resolved.GetProperty("key").GetString(), resolved.GetProperty("unreadCount").GetInt32()));
        Assert.False(resolved.TryGetProperty("item", out _));
        await UsersTestHost.CloseAsync(socket);
    }

    [Fact]
    public async Task EventsSocket_ProtectedServer_EachSocketGetsItsOwnUnreadCount_AUserWithoutThePermissionGetsNothing()
    {
        var (h, tokens) = await StartProtectedAsync(new() { ["admin"] = ["admin:access"], ["plain"] = ["commands:execute"] });
        async Task<WebSocket> OpenAsync(string token)
        {
            var socket = await h.ConnectWebSocketAsync($"/ws/v1/qcli/events?ticket={await h.TicketAsync(token)}");
            Assert.Contains("connected", await UsersTestHost.ReceiveTextAsync(socket));
            return socket;
        }
        var admin = await OpenAsync(tokens["admin"]);
        var plain = await OpenAsync(tokens["plain"]);
        var notifications = h.Service<CliNotificationService>();

        notifications.Notify(Note("public"));
        Assert.Equal(1, (await NextAsync(admin, m => m.GetProperty("key").GetString() == "public")).GetProperty("unreadCount").GetInt32());
        Assert.Equal(1, (await NextAsync(plain, m => m.GetProperty("key").GetString() == "public")).GetProperty("unreadCount").GetInt32());

        notifications.Notify(Note("secret", permission: "admin:access"));
        var secret = await NextAsync(admin, m => m.GetProperty("key").GetString() == "secret");
        Assert.Equal(("created", 2), (secret.GetProperty("action").GetString(), secret.GetProperty("unreadCount").GetInt32()));

        // the admin reads everything: only the admin's sockets hear of it
        await Json(await Send(h.Client, HttpMethod.Post, $"{Url}/read-all", tokens["admin"]));
        Assert.Equal(0, (await NextAsync(admin, m => m.GetProperty("action").GetString() == "read-all")).GetProperty("unreadCount").GetInt32());
        await h.Service<ICliEventSocketManager>().BroadcastMessageAsync("{\"type\":\"marker\"}");
        Assert.Empty(await ChangesUntilMarkerAsync(plain));

        await UsersTestHost.CloseAsync(admin);
        await UsersTestHost.CloseAsync(plain);
    }

    // ---------------------------------------------------------------- command and MCP

    private static async Task<string> Run(HttpClient client, IServiceProvider services, string line, string? token = null) =>
        (await Qodalis.Cli.Tests.Config.ConfigCommands.RunAsync(client, services.GetRequiredService<ICliCommandRegistry>(), line, token)).Text;

    [Fact]
    public async Task Command_ListsReadsAndClears()
    {
        var h = await StartAsync();
        var services = h.Server.Services;
        var n = h.Notifications.Notify(Note("cmd", "error", title: "Something broke"));
        var listed = await Run(h.Client, services, "notifications list");
        Assert.StartsWith("1 notification(s), 1 unread\n* x ", listed);
        Assert.Contains($"Something broke  ({n.Id})", listed);
        Assert.Contains("\n      Details", listed);
        Assert.Contains("Something broke", await Run(h.Client, services, "notifications"));

        Assert.Equal("Read: Something broke", await Run(h.Client, services, $"notifications read {n.Id[..6]}"));
        Assert.Equal("No unread notifications.", await Run(h.Client, services, "notifications list --unread"));
        Assert.Equal("Marked 0 notification(s) read.", await Run(h.Client, services, "notifications read --all"));
        Assert.Equal("Error: notification not found: n_missing", await Run(h.Client, services, "notifications read n_missing"));
        Assert.Equal("Read: Something broke", await Run(h.Client, services, "notifications read cmd"));

        h.Notifications.Notify(Note("other"));
        Assert.Equal("Error: n_ matches 2 notifications; use the full id", await Run(h.Client, services, "notifications read n_"));
        Assert.Contains("About other", await Run(h.Client, services, "notifications list --severity info"));
        Assert.DoesNotContain("About other", await Run(h.Client, services, "notifications list --severity error"));
        h.Notifications.Resolve("other");

        Assert.Equal("Dismissed: Something broke", await Run(h.Client, services, $"notifications clear {n.Id}"));
        Assert.Equal("No notifications.", await Run(h.Client, services, "notifications list"));
    }

    [Fact]
    public async Task Command_ClearAllNeedsNotificationsManage_OnAProtectedServer()
    {
        var (h, tokens) = await StartProtectedAsync(new()
        {
            ["plain"] = ["commands:execute"], ["manager"] = ["commands:execute", "notifications:manage"],
        });
        var n = h.Service<CliNotificationService>().Notify(Note("shared"));
        Assert.Equal("Error: forbidden: missing permission notifications:manage",
            await Run(h.Client, h.Host.Services, $"notifications clear {n.Id} --all", tokens["plain"]));
        Assert.Equal("Cleared for everyone: About shared", await Run(h.Client, h.Host.Services, $"notifications clear {n.Id} --all", tokens["manager"]));
        Assert.Null(h.Service<CliNotificationService>().Get("shared"));
    }

    [Fact]
    public async Task McpTool_ListsTheCallersNotifications()
    {
        var h = await StartAsync(configure: cli => cli.AddMcp());
        h.Notifications.Notify(Note("mcp", title: "From MCP"));
        h.Notifications.Notify(Note("seen"));
        h.Notifications.MarkRead(CliNotificationViewer.Anonymous, h.Notifications.Get("seen")!.Id);

        var tool = (await Mcp.ToolsAsync(h.Client)).Single(t => t.GetProperty("name").GetString() == "notifications_list");
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        var result = await Mcp.CallAsync(h.Client, "notifications_list", new { unread = true });
        Assert.False(result.GetProperty("isError").GetBoolean());
        var structured = result.GetProperty("structuredContent");
        Assert.Equal(1, structured.GetProperty("unreadCount").GetInt32());
        var item = Assert.Single(structured.GetProperty("items").EnumerateArray());
        Assert.Equal(("mcp", "From MCP"), (item.GetProperty("key").GetString(), item.GetProperty("title").GetString()));
        Assert.StartsWith("1 unread\n* [info] From MCP", Mcp.Text(result));
        Assert.Equal(2, (await Mcp.CallAsync(h.Client, "notifications_list")).GetProperty("structuredContent").GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await Mcp.CallAsync(h.Client, "notifications_list", new { limit = 1 })).GetProperty("structuredContent").GetProperty("items").GetArrayLength());
    }

    // ---------------------------------------------------------------- the server's own sources

    [Fact]
    public async Task AnonymousAdminWarning_WhileOn_ResolvedOnceOff()
    {
        var dataDir = TempDir();
        var open = await StartAsync(dataDir, cli => cli.AllowAnonymousAdmin());
        var warning = open.Notifications.Get(CliNotificationKeys.AnonymousAdmin)!;
        Assert.Equal(("security", "warning", "admin:access", "/config", "Anyone can administer this server"),
            (warning.Kind, warning.Severity, warning.RequiredPermission, warning.Link, warning.Title));
        await open.DisposeAsync();
        var closed = await StartAsync(dataDir);
        Assert.Null(closed.Notifications.Get(CliNotificationKeys.AnonymousAdmin));
    }

    [Fact]
    public async Task RestartRequired_FollowsThePendingChanges()
    {
        var h = await StartAsync();
        var restart = h.Service<CliServerRestart>();
        restart.MarkRequired(new("config:cors.cors.mode", "config", "CORS: Mode changed"));
        var n = h.Notifications.Get(CliNotificationKeys.RestartRequired)!;
        Assert.Equal(("restart", "warning", "CORS: Mode changed", "/", "admin:access", "Restart required: 1 change waits for a restart"),
            (n.Kind, n.Severity, n.Message, n.Link, n.RequiredPermission, n.Title));
        Assert.Equal("[\"config:cors.cors.mode\"]", n.Data!["pending"]!.ToJsonString());
        restart.MarkRequired(new("x", "test", "Another"));
        Assert.Equal("Restart required: 2 changes wait for a restart", h.Notifications.Get(CliNotificationKeys.RestartRequired)!.Title);
        Assert.Equal("CORS: Mode changed\nAnother", h.Notifications.Get(CliNotificationKeys.RestartRequired)!.Message);
        restart.ClearRequired("config:cors.cors.mode");
        restart.ClearRequired("x");
        Assert.Null(h.Notifications.Get(CliNotificationKeys.RestartRequired));
    }

    [Fact]
    public async Task ARestartedServer_NothingPending_ResolvesTheRestartNotificationItKept()
    {
        var dataDir = TempDir();
        var first = await StartAsync(dataDir);
        first.Service<CliServerRestart>().MarkRequired(new("x", "test", "Pending"));
        Assert.NotNull(first.Notifications.Get(CliNotificationKeys.RestartRequired));
        await first.DisposeAsync();
        var second = await StartAsync(dataDir);
        Assert.Null(second.Notifications.Get(CliNotificationKeys.RestartRequired));
    }

    [Fact]
    public async Task Plugins_FailedOrNeedingConfiguration_AreReported_AndResolvedAtTheNextStartWhenTheyLoad()
    {
        var dataDir = TempDir();
        var first = await StartAsync(dataDir, cli => cli.AddPlugin(new NotifBrokenEntry(), id: "broken").AddPlugin(new NotifNeedyEntry(), id: "needy"));
        var package = typeof(NotifBrokenEntry).Assembly.GetName().Name!;
        var failed = first.Notifications.Get("plugin.failed:broken")!;
        Assert.Equal(("plugin", "error", "Plugin broken failed to load", "admin:access", $"/plugins/{Uri.EscapeDataString(package)}"),
            (failed.Kind, failed.Severity, failed.Title, failed.RequiredPermission, failed.Link));
        Assert.EndsWith(": cannot connect", failed.Message);
        Assert.StartsWith($"{package}@", failed.Message);
        Assert.Equal(("broken", package, "code", "cannot connect"), (failed.Data!["plugin"]!.GetValue<string>(), failed.Data["package"]!.GetValue<string>(),
            failed.Data["source"]!.GetValue<string>(), failed.Data["error"]!.GetValue<string>()));
        var needs = first.Notifications.Get("plugin.needs-configuration:needy")!;
        Assert.Equal(("warning", "Plugin needy needs configuration", "Set settings.apiKey, then restart the server.", "/config?provider=needy"),
            (needs.Severity, needs.Title, needs.Message, needs.Link));
        Assert.Equal("[\"settings.apiKey\"]", needs.Data!["missing"]!.ToJsonString());
        await first.DisposeAsync();

        // the next start: both load
        var second = await StartAsync(dataDir, cli => cli.AddPlugin(new NotifFineEntry(), id: "broken").AddPlugin(new NotifFineEntry(), id: "needy"));
        Assert.Null(second.Notifications.Get("plugin.failed:broken"));
        Assert.Null(second.Notifications.Get("plugin.needs-configuration:needy"));
    }

    [Fact]
    public async Task PluginsPublishThroughTheirContext_AndTheHostThroughTheBuilder()
    {
        var h = await StartAsync(configure: cli =>
        {
            cli.Notifications.Notify(Note("from-builder"));
            cli.AddPlugin(new NotifTalkerEntry(), id: "talker");
        });
        Assert.Equal("Hello from a plugin", h.Notifications.Get("talker:hello")!.Title);
        Assert.NotNull(h.Notifications.Get("from-builder"));
        Assert.Same(h.Notifications, h.Service<ICliNotifications>());
        // published before the file was known: saved in it all the same
        Assert.Contains("from-builder", File.ReadAllText(h.Notifications.FilePath!));
    }
}
