using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Abstractions.Restart;
using Qodalis.Cli.Config;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Restart;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Config;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>A runtime that records what the process would do; no supervisor unless the environment or files say so.</summary>
internal sealed class FakeRestartRuntime : ICliServerRestartRuntime
{
    public Dictionary<string, string> Env { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = [];
    public List<CliServerRestartCommand> Started { get; } = [];
    public TimeSpan? ShutdownTimeout { get; private set; }
    public bool StartFails { get; set; }

    public IReadOnlyDictionary<string, string> EnvironmentVariables => Env;
    public bool FileExists(string path) => Files.ContainsKey(path);
    public string? ReadFile(string path) => Files.TryGetValue(path, out var text) ? text : null;
    public string? ProcessPath { get; set; } = "/app/Demo";
    public string ProcessName { get; set; } = "Demo";
    public IReadOnlyList<string> CommandLineArgs { get; set; } = ["/app/Demo.dll", "--urls", "http://localhost:8046"];
    public string? EntryAssemblyPath { get; set; } = "/app/Demo.dll";
    public string CurrentDirectory { get; set; } = "/app";

    public void StopHost(int exitCode, TimeSpan shutdownTimeout, Func<Task>? afterStopped = null)
    {
        lock (Calls) Calls.Add($"stop {exitCode}");
        ShutdownTimeout = shutdownTimeout;
        afterStopped?.Invoke().GetAwaiter().GetResult();
    }

    public void SetExitCode(int exitCode)
    {
        lock (Calls) Calls.Add($"exit code {exitCode}");
    }

    public void Start(CliServerRestartCommand command)
    {
        if (StartFails) throw new InvalidOperationException("ENOENT");
        lock (Calls) Calls.Add("start");
        Started.Add(command);
    }
}

/// <summary>
/// Restarting the server: the pending list (MarkRequired / ClearRequired, events), config entries with
/// <c>RequiresRestart</c> marked and cleared, supervisor detection (IIS included), the modes (exit, self, handler, auto;
/// the host is never stopped by the shared test hosts and nothing is started: the runtime is faked), the command a self
/// restart runs, 409 when not supported, 403 without sign-in, permissions on a protected server, audit and events, the
/// graceful shutdown's order and drain on a real Kestrel host of its own, and <c>admin restart</c>.
/// </summary>
public sealed class ServerRestartTests : IAsyncLifetime
{
    private const string Url = "/api/v1/qcli/server/restart";

    private readonly List<IAsyncDisposable> _hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts) await host.DisposeAsync();
    }

    private sealed class Host(IHost host) : IAsyncDisposable
    {
        public IHost Server { get; } = host;
        public HttpClient Client { get; } = host.GetTestClient();
        public T Service<T>() where T : notnull => Server.Services.GetRequiredService<T>();
        public CliServerRestart Restart => Service<CliServerRestart>();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.StopAsync();
            Server.Dispose();
        }
    }

    private async Task<Host> StartAsync(Action<CliServerRestartOptions> restart, bool admin = false, Action<CliBuilder>? configure = null)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(s => s.AddControllers().AddCli(cli =>
            {
                cli.ConfigureRestart(restart);
                configure?.Invoke(cli);
                if (admin) cli.AddAdmin();
            }));
            web.Configure(app =>
            {
                app.UseWebSockets();
                app.UseRouting();
                if (admin) app.UseQodalisAdmin();
                app.UseEndpoints(e => e.MapControllers());
                app.UseCli();
            });
        });
        var host = new Host(await builder.StartAsync());
        _hosts.Add(host);
        return host;
    }

    /// <summary>A protected server (users plugin, admin plugin) with the given restart options; one user per entry.</summary>
    private async Task<(UsersTestHost Host, Dictionary<string, string> Tokens)> StartProtectedAsync(Action<CliServerRestartOptions> restart,
        Dictionary<string, string[]> users)
    {
        var host = await UsersTestHost.StartAsync(o => o.MinPasswordLength = 4,
            configureCli: cli =>
            {
                cli.ConfigureRestart(restart);
                cli.AddAdmin();
            },
            configureApp: app => app.UseQodalisAdmin());
        _hosts.Add(host);
        var tokens = new Dictionary<string, string> { ["root"] = await host.RootTokenAsync() };
        foreach (var (name, permissions) in users) tokens[name] = await host.UserWithPermissionsAsync(tokens["root"], name, permissions);
        return (host, tokens);
    }

    private static List<CliEvent> Events(IServiceProvider services, string pattern = "server:*")
    {
        var seen = new List<CliEvent>();
        services.GetRequiredService<ICliEventBus>().Subscribe(pattern, (e, _) =>
        {
            lock (seen) seen.Add(e);
            return Task.CompletedTask;
        });
        return seen;
    }

    private static async Task<JsonNode> Json(HttpResponseMessage res) => JsonNode.Parse(await res.Content.ReadAsStringAsync())!;

    private static string J(string json) => JsonNode.Parse(json)!.ToJsonString();

    private static Task<HttpResponseMessage> Post(HttpClient client, string? json = null, string? token = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Url);
        if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (token != null) req.Headers.Authorization = new("Bearer", token);
        return client.SendAsync(req);
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string? token = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Url);
        if (token != null) req.Headers.Authorization = new("Bearer", token);
        return client.SendAsync(req);
    }

    // ---------------------------------------------------------------- pending restart list

    [Fact]
    public async Task PendingList_MarksOncePerId_Clears_PublishesRestartRequiredToAdminAccess_IsEmptyAtStart()
    {
        var runtime = new FakeRestartRuntime();
        var h = await StartAsync(o => { o.Mode = CliServerRestartModes.Exit; o.Runtime = runtime; });
        var seen = Events(h.Server.Services);
        var restart = h.Restart;
        Assert.Empty(restart.Pending());
        restart.MarkRequired(new("plugin:x", "x", "X installed"));
        restart.MarkRequired(new("plugin:x", "x", "X installed again"));
        restart.MarkRequired(new("plugin:y", "y", "Y updated"));
        Assert.Equal(["plugin:x X installed", "plugin:y Y updated"], restart.Pending().Select(p => $"{p.Id} {p.Description}"));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", restart.Pending()[0].Since);
        Assert.True(restart.ClearRequired("plugin:x"));
        Assert.False(restart.ClearRequired("plugin:x"));
        Assert.Equal(["server:restart-required 1 admin:access", "server:restart-required 2 admin:access", "server:restart-required 1 admin:access"],
            seen.Select(e => $"{e.Type} {((IEnumerable<CliRestartRequirement>)e.Data["pending"]!).Count()} {e.Audience.Permission}"));
        Assert.Throws<ArgumentException>(() => restart.MarkRequired(new("", "x", "d")));
        // the interface in the container is the same service
        Assert.Same(restart, h.Service<ICliServerRestart>());
    }

    [Fact]
    public async Task MarksAChangedRequiresRestartSetting_AndClearsItWhenSetBackToTheValueAtStart()
    {
        var h = await StartAsync(o => { o.Mode = CliServerRestartModes.Exit; o.Runtime = new FakeRestartRuntime(); }, configure: cli =>
            cli.AddConfigProvider(new CliStoredConfigProvider(new CliStoredConfigProviderOptions
            {
                Id = "app",
                Title = "App",
                Sections =
                [
                    new()
                    {
                        Id = "main",
                        Title = "Main",
                        Settings =
                        [
                            new() { Key = "port", Label = "Listen port", Type = CliConfigEntryTypes.Number, Default = 80, RequiresRestart = true },
                            new() { Key = "name", Type = CliConfigEntryTypes.String, Default = "x" },
                            new() { Key = "key", Type = CliConfigEntryTypes.Secret, RequiresRestart = true },
                        ],
                    },
                ],
            })));
        var config = h.Service<CliConfigService>();
        var restart = h.Restart;
        Task Set(string key, JsonNode? value) =>
            config.UpdateAsync("app", "main", new CliConfigUpdateRequest { Changes = new Dictionary<string, JsonNode?> { [key] = value } }, ConfigTestHost.All);

        await Set("name", "y");
        Assert.Empty(restart.Pending());
        await Set("port", 81);
        var pending = Assert.Single(restart.Pending());
        Assert.Equal(("config:app.main.port", "config", "App: Listen port changed"), (pending.Id, pending.Source, pending.Description));
        await Set("port", 82);
        Assert.Single(restart.Pending());
        await Set("port", 80);
        Assert.Empty(restart.Pending());
        // a secret stays marked: its value is never seen
        await Set("key", "a");
        await Set("key", null);
        Assert.Equal(["config:app.main.key"], restart.Pending().Select(p => p.Id));
    }

    // ---------------------------------------------------------------- supervisor detection

    [Fact]
    public void DetectsPm2SystemdIisKubernetesAndDocker_FromTheEnvironmentFilesAndProcess()
    {
        static string? Detect(Dictionary<string, string>? env = null, Dictionary<string, string>? files = null, string process = "Demo")
        {
            var runtime = new FakeRestartRuntime { ProcessName = process };
            foreach (var (k, v) in env ?? []) runtime.Env[k] = v;
            foreach (var (k, v) in files ?? []) runtime.Files[k] = v;
            return CliServerRestart.DetectSupervisor(runtime);
        }

        Assert.Null(Detect());
        Assert.Equal("pm2", Detect(new() { ["pm_id"] = "0" }));
        Assert.Equal("systemd", Detect(new() { ["INVOCATION_ID"] = "abc" }));
        Assert.Null(Detect(new() { ["INVOCATION_ID"] = "" }));
        Assert.Equal("iis", Detect(new() { ["ASPNETCORE_IIS_HTTPAUTH"] = "anonymous;" }));
        Assert.Equal("iis", Detect(new() { ["ASPNETCORE_IIS_PHYSICAL_PATH"] = @"C:\inetpub\app\" }));
        Assert.Equal("iis", Detect(process: "w3wp"));
        Assert.Equal("iis", Detect(process: "iisexpress"));
        Assert.Equal("kubernetes", Detect(new() { ["KUBERNETES_SERVICE_HOST"] = "10.0.0.1" }));
        Assert.Equal("kubernetes", Detect(files: new() { ["/proc/1/cgroup"] = "0::/kubepods/besteffort/pod1" }));
        Assert.Equal("docker", Detect(files: new() { ["/.dockerenv"] = "" }));
        Assert.Equal("docker", Detect(files: new() { ["/proc/1/cgroup"] = "12:cpu:/docker/abc" }));
        Assert.Equal("pm2", Detect(new() { ["pm_id"] = "1" }, new() { ["/.dockerenv"] = "" }));
    }

    [Fact]
    public void SelfCommand_TheSameExecutableAndArguments_UnderTheDotnetHostDotnetEntryDll_InTheSameDirectory()
    {
        // an apphost (dotnet run, a published app): the executable with the arguments after the program
        var apphost = CliServerRestart.SelfCommand(new FakeRestartRuntime());
        Assert.Equal(("/app/Demo", "--urls http://localhost:8046", "/app"), (apphost.FileName, string.Join(' ', apphost.Arguments), apphost.WorkingDirectory));

        // dotnet app.dll, or dotnet run without an apphost: dotnet <entry dll> <arguments>
        var dotnet = CliServerRestart.SelfCommand(new FakeRestartRuntime
        {
            ProcessPath = "/usr/share/dotnet/dotnet",
            CommandLineArgs = ["/src/demo/bin/Debug/net10.0/Demo.dll", "--flag"],
            EntryAssemblyPath = "/src/demo/bin/Debug/net10.0/Demo.dll",
            CurrentDirectory = "/src/demo",
        });
        Assert.Equal(("/usr/share/dotnet/dotnet", "/src/demo/bin/Debug/net10.0/Demo.dll --flag", "/src/demo"),
            (dotnet.FileName, string.Join(' ', dotnet.Arguments), dotnet.WorkingDirectory));

        var windows = CliServerRestart.SelfCommand(new FakeRestartRuntime
        {
            ProcessPath = @"C:\Program Files\dotnet\dotnet.exe",
            CommandLineArgs = [@"C:\app\Demo.dll"],
            EntryAssemblyPath = null,
            CurrentDirectory = @"C:\app",
        });
        Assert.Equal([@"C:\app\Demo.dll"], windows.Arguments);
    }

    // ---------------------------------------------------------------- modes

    [Fact]
    public async Task AutoWithoutASupervisor_NotSupported_GetSaysWhy_PostAnswers409()
    {
        var runtime = new FakeRestartRuntime();
        var h = await StartAsync(o => { o.Runtime = runtime; o.AllowAnonymous = true; });
        var res = await Get(h.Client);
        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
        Assert.Equal(J($$"""
            {"supported":false,"mode":null,"supervisor":null,"reason":"no supervisor detected; set restart.mode","startedAt":"{{h.Restart.StartedAt}}","pending":[]}
            """), (await Json(res)).ToJsonString());
        var post = await Post(h.Client, "{}");
        Assert.Equal(409, (int)post.StatusCode);
        Assert.Equal(J("""{"error":"restart not supported: no supervisor detected; set restart.mode"}"""), (await Json(post)).ToJsonString());
        Assert.Equal(CliServerRestart.NotSupported, (await Json(post))["error"]!.GetValue<string>());
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task AutoUnderSystemd_StopsTheHostWithExitCode75_AfterTheShutdownSteps()
    {
        var runtime = new FakeRestartRuntime();
        runtime.Env["INVOCATION_ID"] = "x";
        var h = await StartAsync(o => { o.Runtime = runtime; o.AllowAnonymous = true; });
        var status = await Json(await Get(h.Client));
        Assert.Equal((true, "exit", "systemd"), (status["supported"]!.GetValue<bool>(), status["mode"]!.GetValue<string>(), status["supervisor"]!.GetValue<string>()));
        Assert.Null(status["reason"]);
        var res = await Post(h.Client, """{"reason":"apply settings"}""");
        Assert.Equal(202, (int)res.StatusCode);
        Assert.Equal(J("""{"restarting":true,"pending":[]}"""), (await Json(res)).ToJsonString());
        await h.Restart.Completion!;
        Assert.Equal(["stop 75"], runtime.Calls);
        // the host's shutdown timeout: 10 s for the in-flight requests
        Assert.Equal(TimeSpan.FromSeconds(10), runtime.ShutdownTimeout);
    }

    [Fact]
    public async Task Exit_WithACustomExitCodeAndShutdownTimeout_TheSupervisorReportedWithAnExplicitMode()
    {
        var runtime = new FakeRestartRuntime();
        runtime.Files["/.dockerenv"] = "";
        var h = await StartAsync(o =>
        {
            o.Mode = CliServerRestartModes.Exit;
            o.ExitCode = 3;
            o.ShutdownTimeout = TimeSpan.FromSeconds(2);
            o.Runtime = runtime;
            o.AllowAnonymous = true;
        });
        var status = h.Restart.Status();
        Assert.Equal((true, "exit", "docker"), (status.Supported, status.Mode, status.Supervisor));
        Assert.Equal(202, (int)(await Post(h.Client)).StatusCode);
        await h.Restart.Completion!;
        Assert.Equal(["stop 3"], runtime.Calls);
        Assert.Equal(TimeSpan.FromSeconds(2), runtime.ShutdownTimeout);
    }

    [Fact]
    public async Task Self_StopsTheHost_ThenStartsTheSameCommandInTheSameDirectory()
    {
        var runtime = new FakeRestartRuntime();
        var h = await StartAsync(o => { o.Mode = CliServerRestartModes.Self; o.Runtime = runtime; o.AllowAnonymous = true; });
        var status = h.Restart.Status();
        Assert.Equal((true, "self", null), (status.Supported, status.Mode, status.Supervisor));
        Assert.Equal(202, (int)(await Post(h.Client)).StatusCode);
        await h.Restart.Completion!;
        Assert.Equal(["stop 0", "start"], runtime.Calls);
        var command = Assert.Single(runtime.Started);
        Assert.Equal(("/app/Demo", "--urls http://localhost:8046", "/app"), (command.FileName, string.Join(' ', command.Arguments), command.WorkingDirectory));
    }

    [Fact]
    public async Task Self_ExitCode1_WhenTheNewProcessCannotBeStarted()
    {
        var runtime = new FakeRestartRuntime { StartFails = true };
        var h = await StartAsync(o => { o.Mode = CliServerRestartModes.Self; o.Runtime = runtime; o.AllowAnonymous = true; });
        Assert.Equal(202, (int)(await Post(h.Client)).StatusCode);
        await h.Restart.Completion!;
        Assert.Equal(["stop 0", "exit code 1"], runtime.Calls);
    }

    [Fact]
    public async Task Handler_TheHostsFunctionOnceTheHostStopped_WithTheReasonThePendingIdsAndWhoAsked()
    {
        var runtime = new FakeRestartRuntime();
        CliServerRestartContext? received = null;
        var h = await StartAsync(o =>
        {
            o.Mode = CliServerRestartModes.Handler;
            o.Handler = c =>
            {
                lock (runtime.Calls) runtime.Calls.Add("handler");
                received = c;
                return Task.CompletedTask;
            };
            o.Runtime = runtime;
            o.AllowAnonymous = true;
        });
        h.Restart.MarkRequired(new("a", "s", "A"));
        Assert.Equal(202, (int)(await Post(h.Client, """{"reason":"  why  "}""")).StatusCode);
        await h.Restart.Completion!;
        Assert.Equal(("why", "anonymous"), (received!.Reason, received.By));
        Assert.Equal(["a"], received.Pending);
        Assert.Equal(["stop 0", "handler"], runtime.Calls);
    }

    [Fact]
    public void RefusesAHandlerModeWithoutAHandler_AnUnknownMode_AndABadTimeout()
    {
        var services = new ServiceCollection();
        Assert.Equal("restart mode 'handler' needs a Handler (Parameter 'Handler')",
            Assert.Throws<ArgumentException>(() => services.AddControllers().AddCli(cli => cli.ConfigureRestart(o => o.Mode = "handler"))).Message);
        Assert.StartsWith("invalid restart mode: nope",
            Assert.Throws<ArgumentException>(() => services.AddControllers().AddCli(cli => cli.ConfigureRestart(o => o.Mode = "nope"))).Message);
        Assert.StartsWith("invalid restart shutdown timeout",
            Assert.Throws<ArgumentException>(() => new CliServerRestartOptions { ShutdownTimeout = TimeSpan.Zero }.Validate()).Message);
    }

    [Fact]
    public async Task ValidatesTheReason_AndRefusesASecondRestart()
    {
        var runtime = new FakeRestartRuntime();
        var h = await StartAsync(o =>
        {
            o.Mode = CliServerRestartModes.Handler;
            o.Handler = _ => Task.CompletedTask;
            o.Runtime = runtime;
            o.AllowAnonymous = true;
        });
        Assert.Equal(J("""{"error":"reason must be a string"}"""), (await Json(await Post(h.Client, """{"reason":5}"""))).ToJsonString());
        var tooLong = await Post(h.Client, JsonSerializer.Serialize(new { reason = new string('x', 501) }));
        Assert.Equal(400, (int)tooLong.StatusCode);
        Assert.Equal("reason must be at most 500 characters", (await Json(tooLong))["error"]!.GetValue<string>());
        Assert.Equal(400, (int)(await Post(h.Client, "{not json")).StatusCode);
        Assert.Equal(202, (int)(await Post(h.Client)).StatusCode);
        var again = await Post(h.Client);
        Assert.Equal(409, (int)again.StatusCode);
        Assert.Equal(J("""{"error":"restart already in progress"}"""), (await Json(again)).ToJsonString());
        Assert.True(h.Restart.Restarting);
        await h.Restart.Completion!;
    }

    [Fact]
    public async Task Refuses403OnAServerWithoutSignIn_UnlessAllowAnonymous()
    {
        var runtime = new FakeRestartRuntime();
        var h = await StartAsync(o => { o.Mode = CliServerRestartModes.Exit; o.Runtime = runtime; });
        var res = await Post(h.Client);
        Assert.Equal(403, (int)res.StatusCode);
        Assert.StartsWith("restart needs sign-in", (await Json(res))["error"]!.GetValue<string>());
        // the status stays readable
        Assert.True((await Json(await Get(h.Client)))["supported"]!.GetValue<bool>());
        Assert.Empty(runtime.Calls);
        Assert.False(h.Restart.Restarting);
    }

    // ---------------------------------------------------------------- protected server

    [Fact]
    public async Task OnAProtectedServer_GetNeedsAdminAccess_PostServerRestart_AuditsAndPublishesRestarting()
    {
        var runtime = new FakeRestartRuntime();
        var (h, tokens) = await StartProtectedAsync(o => { o.Mode = CliServerRestartModes.Exit; o.Runtime = runtime; },
            new() { ["viewer"] = ["admin:access"], ["restarter"] = ["server:restart"] });
        var restart = h.Host.Services.GetRequiredService<CliServerRestart>();
        var seen = Events(h.Host.Services, "server:restarting");
        restart.MarkRequired(new("config:cors.cors.mode", "config", "CORS: Mode changed"));

        Assert.Equal(401, (int)(await Get(h.Client)).StatusCode);
        Assert.Equal(403, (int)(await Get(h.Client, tokens["restarter"])).StatusCode);
        var status = await Get(h.Client, tokens["viewer"]);
        Assert.Equal(200, (int)status.StatusCode);
        Assert.Equal("CORS: Mode changed", (await Json(status))["pending"]![0]!["description"]!.GetValue<string>());
        var refused = await Post(h.Client, token: tokens["viewer"]);
        Assert.Equal(403, (int)refused.StatusCode);
        Assert.Equal("permission denied: server:restart", (await Json(refused))["error"]!.GetValue<string>());
        Assert.Empty(runtime.Calls);

        var res = await Post(h.Client, """{"reason":"apply CORS"}""", tokens["restarter"]);
        Assert.Equal(202, (int)res.StatusCode);
        Assert.Equal(J("""{"restarting":true,"pending":["config:cors.cors.mode"]}"""), (await Json(res)).ToJsonString());
        await restart.Completion!;
        Assert.Equal(["stop 75"], runtime.Calls);

        var entry = (await h.Host.Services.GetRequiredService<IAuditStore>().GetEntriesAsync()).Single(e => e.Event == "server.restart");
        Assert.Equal(("restarter", "restarter", "server", "apply CORS"), (entry.Username, entry.Actor, entry.Target, entry.Details));
        Assert.NotNull(entry.ActorId);
        Assert.Equal(J("""{"reason":"apply CORS","by":"restarter","pending":["config:cors.cors.mode"],"mode":"exit","via":"api"}"""),
            JsonSerializer.Serialize(entry.Properties));
        var e = Assert.Single(seen);
        Assert.Equal(J("""{"reason":"apply CORS","by":"restarter","pending":["config:cors.cors.mode"],"mode":"exit"}"""), JsonSerializer.Serialize(e.Data));
        Assert.Equal("admin:access", e.Audience.Permission);
    }

    [Fact]
    public async Task ServerRestart_IsInTheCatalog_GroupServer_AndTheRouteTable()
    {
        var h = await StartAsync(o => { o.Mode = CliServerRestartModes.Exit; o.Runtime = new FakeRestartRuntime(); });
        Assert.Equal(new CliPermission("server:restart", "Restart the server", "Server", "cli"), h.Service<ICliPermissionCatalog>().Find("server:restart"));
        Assert.Equal("admin:access", CliRoutePolicy.Resolve("GET", Url).Permission);
        Assert.Equal("server:restart", CliRoutePolicy.Resolve("POST", Url).Permission);
        Assert.Equal(CliRoutePolicy.ServerRestart, CliServerRestartConstants.Permission);
    }

    // ---------------------------------------------------------------- graceful shutdown

    [Fact]
    public async Task TheShutdownSteps_SocketsClosedWith1012_ThenTheHooksInOrder_ThenTheHostsStop()
    {
        var runtime = new FakeRestartRuntime();
        var h = await StartAsync(o => { o.Mode = CliServerRestartModes.Exit; o.Runtime = runtime; o.AllowAnonymous = true; });
        var order = runtime.Calls;
        h.Restart.OnShutdown("a", () =>
        {
            lock (order) order.Add("hook a");
            return Task.CompletedTask;
        });
        h.Restart.OnShutdown("broken", () => throw new InvalidOperationException("boom"));
        h.Restart.OnShutdown("b", async () =>
        {
            await Task.Delay(10);
            lock (order) order.Add("hook b");
        });

        var ws = await h.Server.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws/v1/qcli/events"), CancellationToken.None);
        var messages = new List<string>();
        var buffer = new byte[4096];
        // the welcome message, if any, then wait until the server closes
        var closed = Task.Run(async () =>
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    // answer the close, as a browser does
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return (ws.CloseStatus, ws.CloseStatusDescription);
                }
                messages.Add(Encoding.UTF8.GetString(buffer, 0, r.Count));
            }
        });
        for (var i = 0; i < 50 && h.Service<ICliEventSocketManager>().GetClients().Count == 0; i++) await Task.Delay(20);

        Assert.Equal(202, (int)(await Post(h.Client)).StatusCode);
        await h.Restart.Completion!;
        var (code, reason) = await closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(((WebSocketCloseStatus)1012, "Server restarting"), (code, reason));
        Assert.Contains(messages, m => JsonNode.Parse(m)?["type"]?.GetValue<string>() == "disconnect");
        Assert.Equal(["hook a", "hook b", "stop 75"], order);
    }

    [Fact]
    public async Task OnARealHost_TheStopFinishesInFlightRequests_StopsHostedServices_DisposesServices_WithExitCode75()
    {
        var order = new List<string>();
        void Log(string step)
        {
            lock (order) order.Add(step);
        }
        var app = BuildKestrelApp(cli => cli.ConfigureRestart(o => { o.Mode = CliServerRestartModes.Exit; o.AllowAnonymous = true; }), Log);
        app.MapGet("/slow", async () =>
        {
            await Task.Delay(300);
            Log("slow done");
            return "ok";
        });
        await app.StartAsync();
        var restart = app.Services.GetRequiredService<CliServerRestart>();
        restart.OnShutdown("host", () =>
        {
            Log("hook");
            return Task.CompletedTask;
        });
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() => Log("stopping"));
        lifetime.ApplicationStopped.Register(() => Log("stopped"));
        var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var previousExitCode = Environment.ExitCode;
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
            var slow = client.GetStringAsync("/slow");
            await Task.Delay(50);
            Assert.Equal(202, (int)(await client.PostAsync(Url, null)).StatusCode);
            await restart.Completion!;
            await app.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("ok", await slow);
            Assert.Equal(75, Environment.ExitCode);
            Assert.Equal(TimeSpan.FromSeconds(10), app.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
            await app.DisposeAsync();
        }
        // the shutdown steps, then the host's stop: the in-flight request finished and the hosted services stopped before
        // it stopped, the container's services disposed last
        Assert.Equal(["hook", "stopping"], order.Take(2));
        Assert.True(order.IndexOf("slow done") < order.IndexOf("stopped"));
        Assert.True(order.IndexOf("hosted service stopped") > 1 && order.IndexOf("hosted service stopped") < order.IndexOf("stopped"));
        Assert.Equal("service disposed", order[^1]);
        Assert.Equal(6, order.Count);
    }

    [Fact]
    public async Task OnARealHost_StopsWaitingForAHungRequestAfterTheShutdownTimeout_ThenSelfRunsTheLastStep()
    {
        var steps = new List<string>();
        var runtime = new RecordingProcessRuntime(steps);
        var app = BuildKestrelApp(cli => cli.ConfigureRestart(o =>
        {
            o.Mode = CliServerRestartModes.Self;
            o.ShutdownTimeout = TimeSpan.FromMilliseconds(200);
            o.AllowAnonymous = true;
        }), _ => { }, services => services.AddSingleton<ICliServerRestartRuntime>(sp => runtime.Attach(sp)));
        var hung = new TaskCompletionSource();
        app.MapGet("/hang", async (HttpContext context) => await hung.Task.WaitAsync(context.RequestAborted));
        await app.StartAsync();
        var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var previousExitCode = Environment.ExitCode;
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
            var hangs = client.GetAsync("/hang");
            await Task.Delay(50);
            var started = DateTime.UtcNow;
            Assert.Equal(202, (int)(await client.PostAsync(Url, null)).StatusCode);
            await app.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
            // started once the host stopped (the port free), not before
            Assert.Equal(["start /app/Demo"], steps);
            await Assert.ThrowsAnyAsync<Exception>(() => hangs);
        }
        finally
        {
            hung.TrySetResult();
            Environment.ExitCode = previousExitCode;
            await app.DisposeAsync();
        }
    }

    /// <summary>The real runtime (it stops its host), recording what it would start instead of starting it.</summary>
    private sealed class RecordingProcessRuntime(List<string> steps) : ICliServerRestartRuntime
    {
        private CliServerProcessRuntime? _real;
        private IHostApplicationLifetime? _lifetime;

        public RecordingProcessRuntime Attach(IServiceProvider services)
        {
            _lifetime = services.GetRequiredService<IHostApplicationLifetime>();
            _real = new CliServerProcessRuntime(_lifetime, services.GetService<IOptions<HostOptions>>());
            return this;
        }

        public IReadOnlyDictionary<string, string> EnvironmentVariables => new Dictionary<string, string>();
        public bool FileExists(string path) => false;
        public string? ReadFile(string path) => null;
        public string? ProcessPath => "/app/Demo";
        public string ProcessName => "Demo";
        public IReadOnlyList<string> CommandLineArgs => ["/app/Demo.dll"];
        public string? EntryAssemblyPath => "/app/Demo.dll";
        public string CurrentDirectory => "/app";
        public void StopHost(int exitCode, TimeSpan shutdownTimeout, Func<Task>? afterStopped = null) => _real!.StopHost(exitCode, shutdownTimeout, afterStopped);
        public void SetExitCode(int exitCode) => steps.Add($"exit code {exitCode}");

        public void Start(CliServerRestartCommand command)
        {
            // The host stopped: nothing listens any more
            Assert.True(_lifetime!.ApplicationStopped.IsCancellationRequested);
            lock (steps) steps.Add($"start {command.FileName}");
        }
    }

    private sealed class RecordingHostedService(Action<string> log) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            log("hosted service stopped");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDisposable(Action<string> log) : IDisposable
    {
        public void Dispose() => log("service disposed");
    }

    /// <summary>A Kestrel host on a free port of its own (never the shared test hosts), with a hosted service and a disposable singleton.</summary>
    private static WebApplication BuildKestrelApp(Action<CliBuilder> configure, Action<string> log, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddCli(configure);
        builder.Services.AddHostedService(_ => new RecordingHostedService(log));
        builder.Services.AddSingleton(_ => new RecordingDisposable(log));
        services?.Invoke(builder.Services);
        var app = builder.Build();
        _ = app.Services.GetRequiredService<RecordingDisposable>();
        app.UseWebSockets();
        app.MapControllers();
        app.UseCli();
        return app;
    }

    // ---------------------------------------------------------------- admin restart

    [Fact]
    public async Task AdminRestart_WithoutYesPrintsWhatIsPendingAndHowToConfirm_WithYesRestarts()
    {
        var runtime = new FakeRestartRuntime();
        var (h, tokens) = await StartProtectedAsync(o => { o.Mode = CliServerRestartModes.Self; o.Runtime = runtime; }, []);
        var restart = h.Host.Services.GetRequiredService<CliServerRestart>();
        var registry = h.Host.Services.GetRequiredService<ICliCommandRegistry>();
        Task<(int Status, string Text, JsonElement Body)> Run(string line, string token) => ConfigCommands.RunAsync(h.Client, registry, line, token);
        restart.MarkRequired(new("config:cors.cors.mode", "config", "CORS: Mode changed"));

        var preview = (await Run("admin restart", tokens["root"])).Text;
        Assert.Contains("1 change(s) take effect after a restart:", preview);
        Assert.Contains("  - CORS: Mode changed (config, since ", preview);
        Assert.Contains("Restart mode: self (starts itself again)", preview);
        Assert.Contains("Run admin restart --yes [--reason \"<why>\"] to restart the server now.", preview);
        Assert.Empty(runtime.Calls);

        Assert.Matches(@"^Running since \d{4}-\d\d-\d\d \d\d:\d\d:\d\d\nRestart mode: self", (await Run("admin restart status", tokens["root"])).Text);
        var json = JsonNode.Parse((await Run("admin restart status --json", tokens["root"])).Text)!;
        Assert.Equal((true, "self", "config:cors.cors.mode"),
            (json["supported"]!.GetValue<bool>(), json["mode"]!.GetValue<string>(), json["pending"]![0]!["id"]!.GetValue<string>()));

        Assert.Equal("Restarting the server to apply 1 pending change(s) (self mode). The connection drops and comes back once it runs again.",
            (await Run("admin restart --yes --reason \"apply CORS\"", tokens["root"])).Text);
        await restart.Completion!;
        Assert.Equal(["stop 0", "start"], runtime.Calls);
        var entry = (await h.Host.Services.GetRequiredService<IAuditStore>().GetEntriesAsync()).Single(e => e.Event == "server.restart");
        Assert.Equal(("apply CORS", "root", "command"), ((string?)entry.Properties!["reason"], (string?)entry.Properties["by"], (string?)entry.Properties["via"]));
    }

    [Fact]
    public async Task AdminRestart_StatusNeedsOnlyAdminAccess_RestartingNeedsServerRestart()
    {
        var runtime = new FakeRestartRuntime();
        var (h, tokens) = await StartProtectedAsync(o => { o.Mode = CliServerRestartModes.Exit; o.Runtime = runtime; },
            new() { ["viewer"] = ["admin:access", "commands:execute"] });
        var registry = h.Host.Services.GetRequiredService<ICliCommandRegistry>();
        var admin = registry.FindProcessor("admin")!;
        var restartCommand = admin.Processors!.Single(p => p.Command == "restart");
        Assert.Null(restartCommand.RequiredPermissions);
        Assert.Equal(["status"], restartCommand.Processors!.Select(p => p.Command));
        Assert.Contains("No changes are waiting for a restart.", (await ConfigCommands.RunAsync(h.Client, registry, "admin restart status", tokens["viewer"])).Text);
        Assert.Equal("Error: forbidden: missing permission server:restart", (await ConfigCommands.RunAsync(h.Client, registry, "admin restart --yes", tokens["viewer"])).Text);
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task AdminRestart_SaysWhenRestartingIsNotSupported()
    {
        var (h, tokens) = await StartProtectedAsync(o => o.Runtime = new FakeRestartRuntime(), []);
        var registry = h.Host.Services.GetRequiredService<ICliCommandRegistry>();
        Assert.Contains("Error: restart not supported: no supervisor detected; set restart.mode",
            (await ConfigCommands.RunAsync(h.Client, registry, "admin restart --yes", tokens["root"])).Text);
        Assert.Contains("Restart from here: not supported", (await ConfigCommands.RunAsync(h.Client, registry, "admin restart status", tokens["root"])).Text);
    }

    [Fact]
    public async Task AdminRestart_OnAServerWithoutSignIn_RefusedUnlessAllowAnonymous()
    {
        var runtime = new FakeRestartRuntime();
        var h = await StartAsync(o => { o.Mode = CliServerRestartModes.Exit; o.Runtime = runtime; }, admin: true);
        var registry = h.Service<ICliCommandRegistry>();
        Assert.StartsWith("Error: restart needs sign-in", (await ConfigCommands.RunAsync(h.Client, registry, "admin restart --yes")).Text);
        Assert.Empty(runtime.Calls);
    }
}
