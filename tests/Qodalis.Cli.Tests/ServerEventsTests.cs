using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.FileSystem;
using Qodalis.Cli.Plugin.Audit;
using Qodalis.Cli.Plugin.Audit.Extensions;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Plugin.Jobs;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>Records the events of a bus (in-process subscriber).</summary>
internal sealed class EventRecorder : IDisposable
{
    private readonly IDisposable _subscription;

    public EventRecorder(ICliEventBus bus, string pattern = "*")
    {
        _subscription = bus.Subscribe(pattern, (e, _) =>
        {
            Events.Enqueue(e);
            return Task.CompletedTask;
        });
    }

    public ConcurrentQueue<CliEvent> Events { get; } = new();

    public List<CliEvent> OfType(string type) => Events.Where(e => e.Type == type).ToList();

    /// <summary>Waits for an event of <paramref name="type"/> (matching <paramref name="match"/>).</summary>
    public async Task<CliEvent> WaitAsync(string type, Func<CliEvent, bool>? match = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var found = Events.FirstOrDefault(e => e.Type == type && (match == null || match(e)));
            if (found != null) return found;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"no {type} event; got {string.Join(", ", Events.Select(e => e.Type))}");
            await Task.Delay(10);
        }
    }

    public void Dispose() => _subscription.Dispose();
}

// ---------------------------------------------------------------------------
// The bus: patterns, handler isolation, wire format
// ---------------------------------------------------------------------------

public class CliEventBusTests
{
    [Theory]
    [InlineData("job:completed", "*", true)]
    [InlineData("job:completed", "job:*", true)]
    [InlineData("job:completed", "job:completed", true)]
    [InlineData("job:completed", "job:failed", false)]
    [InlineData("job:completed", "user:*", false)]
    [InlineData("jobs:x", "job:*", false)]
    [InlineData("job", "job:*", false)]
    [InlineData("job:runs-deleted", "job:runs-*", false)]
    public void Pattern_Matches(string type, string pattern, bool expected) =>
        Assert.Equal(expected, CliEventPattern.Matches(type, pattern));

    [Fact]
    public async Task Publish_DeliversToMatchingHandlers_InSubscriptionOrder()
    {
        var bus = new CliEventBus();
        var calls = new List<string>();
        bus.Subscribe("*", (e, _) => { calls.Add($"all:{e.Type}"); return Task.CompletedTask; });
        bus.Subscribe("job:*", (e, _) => { calls.Add($"jobs:{e.Type}"); return Task.CompletedTask; });
        bus.Subscribe("user:created", (e, _) => { calls.Add($"user:{e.Type}"); return Task.CompletedTask; });

        await bus.PublishAsync("job:started");
        await bus.PublishAsync("user:created");

        Assert.Equal(["all:job:started", "jobs:job:started", "all:user:created", "user:user:created"], calls);
    }

    [Fact]
    public async Task Publish_HandlerErrors_AreLoggedAndDoNotStopDelivery()
    {
        var logger = new Mock<ILogger<CliEventBus>>();
        var bus = new CliEventBus(logger.Object);
        var delivered = new List<string>();
        bus.Subscribe("*", (_, _) => throw new InvalidOperationException("sync boom"));
        bus.Subscribe("*", async (_, _) =>
        {
            await Task.Yield();
            throw new InvalidOperationException("async boom");
        });
        bus.Subscribe("*", (e, _) => { delivered.Add(e.Type); return Task.CompletedTask; });

        await bus.PublishAsync("test:event");

        Assert.Equal(["test:event"], delivered);
        logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.IsAny<InvalidOperationException>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Subscribe_Dispose_StopsDelivery()
    {
        var bus = new CliEventBus();
        var count = 0;
        var subscription = bus.Subscribe("*", (_, _) => { count++; return Task.CompletedTask; });
        await bus.PublishAsync("a:b");
        subscription.Dispose();
        await bus.PublishAsync("a:b");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Publish_ObjectData_IsCamelCase_WithClrValues_AndAudience()
    {
        var bus = new CliEventBus();
        using var recorder = new EventRecorder(bus);

        await bus.PublishAsync("job:completed", new { JobId = "cleanup", Duration = 1520L, Ok = true, Nested = new { A = 1 } },
            CliEventAudience.WithPermission("jobs:read"));

        var e = Assert.Single(recorder.Events);
        Assert.Equal("cleanup", e.Data["jobId"]);
        Assert.Equal(1520L, e.Data["duration"]);
        Assert.Equal(true, e.Data["ok"]);
        Assert.Equal(1, ((JsonElement)e.Data["nested"]!).GetProperty("a").GetInt32());
        Assert.Equal(new CliEventAudience("jobs:read"), e.Audience);
        Assert.Equal(DateTimeKind.Utc, e.Timestamp.Kind);
        Assert.True(DateTime.UtcNow - e.Timestamp < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Serialize_IsFlat_TypeAndTimestampReserved()
    {
        var bus = new CliEventBus();
        using var recorder = new EventRecorder(bus);

        await bus.PublishAsync("job:failed", new Dictionary<string, object?>
        {
            ["type"] = "spoofed",
            ["timestamp"] = new DateTime(2026, 11, 6, 10, 15, 0, DateTimeKind.Utc),
            ["jobId"] = "cleanup",
            ["executionId"] = "e1",
            ["error"] = "boom",
            ["actor"] = null,
        });

        var json = JsonSerializer.Deserialize<JsonElement>(CliEventBus.Serialize(Assert.Single(recorder.Events)));
        Assert.Equal("job:failed", json.GetProperty("type").GetString());
        Assert.Equal("2026-11-06T10:15:00.000Z", json.GetProperty("timestamp").GetString());
        Assert.Equal("cleanup", json.GetProperty("jobId").GetString());
        Assert.Equal("e1", json.GetProperty("executionId").GetString());
        Assert.Equal("boom", json.GetProperty("error").GetString());
        Assert.False(json.TryGetProperty("data", out _));
        Assert.False(json.TryGetProperty("audience", out _));
        Assert.Equal(6, json.EnumerateObject().Count()); // type, timestamp, jobId, executionId, error, actor (null)
    }

    [Fact]
    public async Task Publish_EpochMillisecondsTimestamp_BecomesTheEventTime()
    {
        var bus = new CliEventBus();
        using var recorder = new EventRecorder(bus);
        await bus.PublishAsync("audit:recorded", new { timestamp = 1_762_424_100_000L, @event = "login" });

        var e = Assert.Single(recorder.Events);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_762_424_100_000L).UtcDateTime, e.Timestamp);
        Assert.False(e.Data.ContainsKey("timestamp"));
        Assert.Equal("login", e.Data["event"]);
    }

    [Fact]
    public async Task Publish_NonObjectData_Throws()
    {
        var bus = new CliEventBus();
        await Assert.ThrowsAsync<ArgumentException>(() => bus.PublishAsync("a:b", 42));
    }

    [Fact]
    public void AddCli_RegistersOneBus()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddCli();
        using var sp = services.BuildServiceProvider();
        Assert.IsType<CliEventBus>(sp.GetRequiredService<ICliEventBus>());
        Assert.Same(sp.GetRequiredService<ICliEventBus>(), sp.GetRequiredService<ICliEventBus>());
    }
}

// ---------------------------------------------------------------------------
// Audience: delivery over a real events socket
// ---------------------------------------------------------------------------

public class ServerEventsAudienceTests
{
    /// <summary>Reads until the <c>marker</c> message; returns the types received before it.</summary>
    private static async Task<List<string>> TypesUntilMarkerAsync(WebSocket socket)
    {
        var types = new List<string>();
        while (true)
        {
            var json = JsonSerializer.Deserialize<JsonElement>(await UsersTestHost.ReceiveTextAsync(socket));
            var type = json.GetProperty("type").GetString()!;
            if (type == "marker") return types;
            types.Add(type);
        }
    }

    [Fact]
    public async Task UnprotectedServer_EverySocketReceivesEveryEvent_Flat()
    {
        await using var h = await McpPlainHost.StartAsync(_ => { });
        var socket = await h.Host.GetTestServer().CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws/v1/qcli/events"), CancellationToken.None);
        Assert.Contains("connected", await UsersTestHost.ReceiveTextAsync(socket));

        var bus = h.Host.Services.GetRequiredService<ICliEventBus>();
        await bus.PublishAsync("test:permission", new { value = 1 }, CliEventAudience.WithPermission("jobs:read"));
        await bus.PublishAsync("test:user", new { value = 2 }, CliEventAudience.User("u1"));

        var first = JsonSerializer.Deserialize<JsonElement>(await UsersTestHost.ReceiveTextAsync(socket));
        Assert.Equal("test:permission", first.GetProperty("type").GetString());
        Assert.Equal(1, first.GetProperty("value").GetInt32());
        Assert.True(first.TryGetProperty("timestamp", out _));
        var second = JsonSerializer.Deserialize<JsonElement>(await UsersTestHost.ReceiveTextAsync(socket));
        Assert.Equal("test:user", second.GetProperty("type").GetString());

        await UsersTestHost.CloseAsync(socket);
    }

    [Fact]
    public async Task ProtectedServer_DeliversByPermission_User_OrEither()
    {
        await using var h = await UsersTestHost.StartAsync(realEventSockets: true);
        var root = await h.RootTokenAsync();
        var reader = await h.UserWithPermissionsAsync(root, "reader", "jobs:read");
        await h.CreateUserAsync(root, "plain");
        var (plain, plainSession) = await h.LoginAsync("plain", "pass1234");
        var plainId = plainSession.GetProperty("user").GetProperty("id").GetString()!;

        async Task<WebSocket> OpenAsync(string token)
        {
            var socket = await h.ConnectWebSocketAsync($"/ws/v1/qcli/events?ticket={await h.TicketAsync(token)}");
            Assert.Contains("connected", await UsersTestHost.ReceiveTextAsync(socket));
            return socket;
        }

        var rootSocket = await OpenAsync(root);
        var readerSocket = await OpenAsync(reader);
        var plainSocket = await OpenAsync(plain);

        var bus = h.Service<ICliEventBus>();
        await bus.PublishAsync("test:everyone");
        await bus.PublishAsync("test:permission", audience: CliEventAudience.WithPermission("jobs:read"));
        await bus.PublishAsync("test:user", audience: CliEventAudience.User(plainId));
        await bus.PublishAsync("test:either", audience: new CliEventAudience("jobs:read", plainId));
        await bus.PublishAsync("test:nobody", audience: CliEventAudience.WithPermission("nobody:has-this"));
        await h.Service<ICliEventSocketManager>().BroadcastMessageAsync("{\"type\":\"marker\"}");

        var rootTypes = (await TypesUntilMarkerAsync(rootSocket)).Where(t => t.StartsWith("test:")).ToList();
        var readerTypes = (await TypesUntilMarkerAsync(readerSocket)).Where(t => t.StartsWith("test:")).ToList();
        var plainTypes = (await TypesUntilMarkerAsync(plainSocket)).Where(t => t.StartsWith("test:")).ToList();

        Assert.Equal(["test:everyone", "test:permission", "test:either", "test:nobody"], rootTypes); // admin: *
        Assert.Equal(["test:everyone", "test:permission", "test:either"], readerTypes);
        Assert.Equal(["test:everyone", "test:user", "test:either"], plainTypes);

        await UsersTestHost.CloseAsync(rootSocket);
        await UsersTestHost.CloseAsync(readerSocket);
        await UsersTestHost.CloseAsync(plainSocket);
    }

    [Fact]
    public async Task ProtectedServer_UsersEvents_ReachOnlyPermittedSockets()
    {
        await using var h = await UsersTestHost.StartAsync(realEventSockets: true);
        var root = await h.RootTokenAsync();
        await h.CreateUserAsync(root, "plain");
        var (plain, _) = await h.LoginAsync("plain", "pass1234");

        var rootSocket = await h.ConnectWebSocketAsync($"/ws/v1/qcli/events?ticket={await h.TicketAsync(root)}");
        var plainSocket = await h.ConnectWebSocketAsync($"/ws/v1/qcli/events?ticket={await h.TicketAsync(plain)}");
        await UsersTestHost.ReceiveTextAsync(rootSocket);
        await UsersTestHost.ReceiveTextAsync(plainSocket);

        await h.CreateUserAsync(root, "someone");
        await h.Service<ICliEventSocketManager>().BroadcastMessageAsync("{\"type\":\"marker\"}");

        Assert.Contains("user:created", await TypesUntilMarkerAsync(rootSocket));
        Assert.Empty(await TypesUntilMarkerAsync(plainSocket));

        await UsersTestHost.CloseAsync(rootSocket);
        await UsersTestHost.CloseAsync(plainSocket);
    }

    /// <summary>An open socket that accepts the first send (<c>connected</c>) and never completes the later ones.</summary>
    private sealed class StalledWebSocket : WebSocket
    {
        private int _sends;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct) =>
            Interlocked.Increment(ref _sends) == 1 ? Task.CompletedTask : Task.Delay(Timeout.Infinite, ct);
    }

    [Fact]
    public async Task Publish_DoesNotWaitForASlowSocket()
    {
        var bus = new CliEventBus();
        using var manager = new CliEventSocketManager(NullLogger<CliEventSocketManager>.Instance, bus);
        using var cts = new CancellationTokenSource();
        _ = manager.HandleConnectionAsync(new StalledWebSocket(), cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (manager.GetClients().Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);

        var publish = Task.WhenAll(Enumerable.Range(0, 3).Select(i => bus.PublishAsync($"test:slow-{i}")));
        Assert.Same(publish, await Task.WhenAny(publish, Task.Delay(2000)));
        cts.Cancel();
    }

    [Fact]
    public void CanReceive_ReadsRequireAuthentication_Live()
    {
        var options = new CliServerOptions();
        var manager = new CliEventSocketManager(NullLogger<CliEventSocketManager>.Instance, new CliEventBus(), options);
        Assert.True(manager.CanReceive(CliEventAudience.WithPermission("jobs:read"), null));
        options.RequireAuthentication = true;
        Assert.False(manager.CanReceive(CliEventAudience.WithPermission("jobs:read"), null));
    }

    [Fact]
    public void CanReceive_WithoutPrincipal_OnlyEveryoneOnAProtectedServer()
    {
        var bus = new CliEventBus();
        var open = new CliEventSocketManager(NullLogger<CliEventSocketManager>.Instance, bus, new CliServerOptions());
        var protectedServer = new CliEventSocketManager(NullLogger<CliEventSocketManager>.Instance, bus,
            new CliServerOptions { RequireAuthentication = true });
        var reader = new CliPrincipal("u1", "reader", ["jobs:*"]);

        Assert.True(open.CanReceive(CliEventAudience.WithPermission("jobs:read"), null));
        Assert.True(protectedServer.CanReceive(CliEventAudience.Everyone, null));
        Assert.False(protectedServer.CanReceive(CliEventAudience.WithPermission("jobs:read"), null));
        Assert.True(protectedServer.CanReceive(CliEventAudience.WithPermission("jobs:read"), reader));
        Assert.False(protectedServer.CanReceive(CliEventAudience.WithPermission("users:read"), reader));
        Assert.True(protectedServer.CanReceive(CliEventAudience.User("u1"), reader));
        Assert.False(protectedServer.CanReceive(CliEventAudience.User("u2"), reader));
        Assert.True(protectedServer.CanReceive(new CliEventAudience("users:read", "u1"), reader));
    }
}

// ---------------------------------------------------------------------------
// Catalog: each event fires from its action
// ---------------------------------------------------------------------------

public class ServerEventsJobsTests
{
    private sealed class OkJob : ICliJob
    {
        public Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingJob : ICliJob
    {
        public Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    private static (CliJobScheduler Scheduler, EventRecorder Recorder, InMemoryJobStorageProvider Storage) Scheduler(
        ICliJob job, CliJobOptions options)
    {
        var bus = new CliEventBus();
        var storage = new InMemoryJobStorageProvider();
        var scheduler = new CliJobScheduler(storage, bus, NullLogger<CliJobScheduler>.Instance);
        scheduler.Register(job, options);
        return (scheduler, new EventRecorder(bus), storage);
    }

    [Fact]
    public async Task Scheduler_PublishesFlatRunEvents_AfterSavingTheRun()
    {
        var (scheduler, recorder, storage) = Scheduler(new OkJob(), new CliJobOptions { Id = "ok", Interval = TimeSpan.FromHours(1) });
        await scheduler.TriggerAsync("ok");

        var started = await recorder.WaitAsync("job:started");
        var completed = await recorder.WaitAsync("job:completed");
        Assert.Equal(JobEvents.Audience, completed.Audience);
        Assert.Equal("ok", started.Data["jobId"]);
        Assert.Equal(started.Data["executionId"], completed.Data["executionId"]);
        Assert.IsType<long>(completed.Data["duration"]);

        var json = JsonSerializer.Deserialize<JsonElement>(CliEventBus.Serialize(completed));
        Assert.Equal("ok", json.GetProperty("jobId").GetString());
        Assert.False(json.TryGetProperty("data", out _));

        var run = await storage.GetExecutionAsync((string)completed.Data["executionId"]!);
        Assert.Equal(JobExecutionStatus.Completed, run!.Status);
        scheduler.Dispose();
    }

    [Fact]
    public async Task Scheduler_PublishesFailedAndRetrying()
    {
        var (scheduler, recorder, _) = Scheduler(new FailingJob(), new CliJobOptions
        {
            Id = "bad", Interval = TimeSpan.FromHours(1), MaxRetries = 1, RetryDelay = TimeSpan.FromMilliseconds(1),
        });
        await scheduler.TriggerAsync("bad");

        var failed = await recorder.WaitAsync("job:failed");
        Assert.Equal("boom", failed.Data["error"]);
        var retrying = await recorder.WaitAsync("job:retrying");
        Assert.Equal(1L, retrying.Data["attempt"]);
        Assert.Equal(1L, retrying.Data["maxRetries"]);
        Assert.True(retrying.Data.ContainsKey("delayMs"));
        scheduler.Dispose();
    }

    [Fact]
    public async Task Scheduler_PauseResumeStop_CarryStatus()
    {
        var (scheduler, recorder, _) = Scheduler(new OkJob(), new CliJobOptions { Id = "j", Interval = TimeSpan.FromHours(1) });
        await scheduler.PauseAsync("j");
        await scheduler.ResumeAsync("j");
        await scheduler.StopJobAsync("j");

        Assert.Equal("paused", recorder.OfType("job:paused").Single().Data["status"]);
        Assert.Equal("active", recorder.OfType("job:resumed").Single().Data["status"]);
        Assert.Equal("stopped", recorder.OfType("job:stopped").Single().Data["status"]);
        Assert.All(recorder.Events, e => Assert.Equal("j", e.Data["jobId"]));
        scheduler.Dispose();
    }

    [Fact]
    public async Task LegacySocketConstructor_BroadcastsFlatMessages()
    {
        var sockets = new FakeEventSocketManager();
        var scheduler = new CliJobScheduler(new InMemoryJobStorageProvider(), sockets, NullLogger<CliJobScheduler>.Instance);
        scheduler.Register(new OkJob(), new CliJobOptions { Id = "j", Interval = TimeSpan.FromHours(1) });
        await scheduler.PauseAsync("j");

        var json = JsonSerializer.Deserialize<JsonElement>(Assert.Single(sockets.Messages));
        Assert.Equal("job:paused", json.GetProperty("type").GetString());
        Assert.Equal("j", json.GetProperty("jobId").GetString());
        Assert.Equal("paused", json.GetProperty("status").GetString());
        scheduler.Dispose();
    }

    [Fact]
    public async Task JobsApi_PublishesTriggeredUpdatedRunsDeleted_ForACustomManager()
    {
        var manager = new Mock<ICliJobManager>();
        manager.Setup(m => m.GetJobsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            [new CliJobInfo { Id = "custom", Name = "custom" }]);
        manager.Setup(m => m.DeleteExecutionsAsync("custom", null, It.IsAny<CancellationToken>())).ReturnsAsync(3);
        manager.Setup(m => m.GetJobAsync("custom", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliJobInfo { Id = "custom", Name = "custom" });
        var bus = new CliEventBus();
        using var recorder = new EventRecorder(bus);
        var controller = new CliJobsController(manager.Object, bus);

        await controller.TriggerJob("custom", CancellationToken.None);
        await controller.UpdateJob("custom", new UpdateJobRequest { Description = "new" }, CancellationToken.None);
        await controller.DeleteExecution("custom", "e1", CancellationToken.None);
        await controller.DeleteHistory("custom", null, CancellationToken.None);

        Assert.Equal(["job:triggered", "job:updated", "job:runs-deleted", "job:runs-deleted"], recorder.Events.Select(e => e.Type));
        Assert.All(recorder.Events, e => Assert.Equal("custom", e.Data["jobId"]));
        Assert.All(recorder.Events, e => Assert.Equal(JobEvents.Audience, e.Audience));
        var runs = recorder.OfType("job:runs-deleted");
        Assert.Equal("e1", runs[0].Data["executionId"]);
        Assert.Equal(3, runs[1].Data["count"]);
    }

    [Fact]
    public async Task JobsApi_FailedAction_PublishesNothing()
    {
        var manager = new Mock<ICliJobManager>();
        manager.Setup(m => m.TriggerAsync("x", It.IsAny<CancellationToken>())).ThrowsAsync(new KeyNotFoundException());
        var bus = new CliEventBus();
        using var recorder = new EventRecorder(bus);

        await new CliJobsController(manager.Object, bus).TriggerJob("x", CancellationToken.None);

        Assert.Empty(recorder.Events);
    }

    [Fact]
    public async Task JobsMcpTools_PublishTriggeredAndRunsDeleted_Once()
    {
        await using var h = await McpPlainHost.StartAsync(cli =>
        {
            cli.AddJob(new OkJob(), o =>
            {
                o.Id = "mcp-job";
                o.Interval = TimeSpan.FromHours(1);
            });
            cli.AddMcp();
        });
        using var recorder = new EventRecorder(h.Host.Services.GetRequiredService<ICliEventBus>(), "job:*");

        Assert.False((await Mcp.CallAsync(h.Client, "jobs_trigger", new { id = "mcp-job" })).GetProperty("isError").GetBoolean());
        await recorder.WaitAsync("job:completed");
        Assert.False((await Mcp.CallAsync(h.Client, "jobs_delete_runs", new { id = "mcp-job" })).GetProperty("isError").GetBoolean());

        Assert.Single(recorder.OfType("job:triggered"));
        var deleted = Assert.Single(recorder.OfType("job:runs-deleted"));
        Assert.Equal(1L, Convert.ToInt64(deleted.Data["count"]));
    }
}

public class ServerEventsUsersTests : IAsyncLifetime
{
    private UsersTestHost _h = null!;
    private EventRecorder _events = null!;
    private string _root = null!;

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync();
        _events = new EventRecorder(_h.Service<ICliEventBus>());
        _root = await _h.RootTokenAsync();
    }

    public async Task DisposeAsync()
    {
        _events.Dispose();
        await _h.DisposeAsync();
    }

    private static string? Str(CliEvent e, string key) => e.Data.TryGetValue(key, out var v) ? v as string : null;

    [Fact]
    public async Task Login_PublishesSessionOpened_AndAuditRecorded()
    {
        var opened = await _events.WaitAsync("session:opened");
        Assert.Equal("root", Str(opened, "username"));
        Assert.NotNull(Str(opened, "sessionId"));
        Assert.NotNull(Str(opened, "userId"));
        Assert.Equal(CliEventAudience.WithPermission("sessions:read"), opened.Audience);

        var audit = await _events.WaitAsync("audit:recorded", e => Str(e, "event") == Qodalis.Cli.Plugin.Users.Models.AuditEvents.Login);
        Assert.Equal("root", Str(audit, "username"));
        Assert.NotNull(Str(audit, "id"));
        Assert.False(audit.Data.ContainsKey("timestamp")); // the entry's time is the event's
        Assert.Equal(CliEventAudience.WithPermission("audit:read"), audit.Audience);
    }

    [Fact]
    public async Task Logout_And_Revoke_PublishSessionClosed()
    {
        var (token, session) = await _h.LoginAsync("root", "root");
        Assert.Equal(204, (int)(await _h.DeleteAsync("sessions/current", token)).StatusCode);
        await _events.WaitAsync("session:closed", e => Str(e, "sessionId") == session.GetProperty("id").GetString());

        var (_, other) = await _h.LoginAsync("root", "root");
        var otherId = other.GetProperty("id").GetString();
        Assert.Equal(204, (int)(await _h.DeleteAsync($"sessions/{otherId}", _root)).StatusCode);
        var revoked = await _events.WaitAsync("session:closed", e => Str(e, "sessionId") == otherId);
        Assert.Equal("root", Str(revoked, "actor"));
    }

    [Fact]
    public async Task IdleSweep_PublishesSessionClosed()
    {
        var (_, session) = await _h.LoginAsync("root", "root");
        _h.Time.Advance(TimeSpan.FromDays(2));
        await _h.Service<Qodalis.Cli.Plugin.Users.Services.AuthService>().SweepIdleSessionsAsync();
        await _events.WaitAsync("session:closed", e => Str(e, "sessionId") == session.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Users_CreateUpdatePasswordMembershipDelete()
    {
        var user = await _h.CreateUserAsync(_root, "evt-user");
        var id = user.GetProperty("id").GetString();
        var created = await _events.WaitAsync("user:created");
        Assert.Equal(id, Str(created, "userId"));
        Assert.Equal("evt-user", Str(created, "username"));
        Assert.Equal("root", Str(created, "actor"));
        Assert.Equal(CliEventAudience.WithPermission("users:read"), created.Audience);

        Assert.Equal(200, (int)(await _h.PutAsync($"users/{id}", _root, new { email = "new@test.com" })).StatusCode);
        Assert.Single(_events.OfType("user:updated"));

        var password = await _h.PutAsync($"users/{id}/password", _root, new { password = "another-pass-1" });
        Assert.True(password.IsSuccessStatusCode, $"password change: {(int)password.StatusCode}");
        Assert.Equal(2, _events.OfType("user:updated").Count);

        await _h.CreateGroupAsync(_root, "evt-group", "jobs:read");
        Assert.Equal(204, (int)(await _h.PostAsync($"groups/evt-group/members/{id}", _root)).StatusCode);
        Assert.Equal(3, _events.OfType("user:updated").Count);

        Assert.Equal(204, (int)(await _h.DeleteAsync($"users/{id}", _root)).StatusCode);
        var deleted = await _events.WaitAsync("user:deleted");
        Assert.Equal(id, Str(deleted, "userId"));
    }

    [Fact]
    public async Task Groups_CreateUpdateDelete()
    {
        await _h.CreateGroupAsync(_root, "evt-g", "jobs:read");
        Assert.Equal(200, (int)(await _h.PutAsync("groups/evt-g", _root, new { description = "d" })).StatusCode);
        Assert.Equal(204, (int)(await _h.DeleteAsync("groups/evt-g", _root)).StatusCode);

        var types = _events.Events.Where(e => e.Type.StartsWith("group:")).Select(e => e.Type).ToList();
        Assert.Equal(["group:created", "group:updated", "group:deleted"], types);
        Assert.All(_events.Events.Where(e => e.Type.StartsWith("group:")), e =>
        {
            Assert.Equal("evt-g", Str(e, "group"));
            Assert.Equal(CliEventAudience.WithPermission("groups:read"), e.Audience);
        });
    }

    [Fact]
    public async Task Tokens_CreatedAndRevoked_ToTokensReadOrTheOwner()
    {
        var res = await _h.PostAsync("tokens", _root, new { name = "evt-token" });
        Assert.Equal(201, (int)res.StatusCode);
        var tokenId = (await UsersTestHost.ReadJsonAsync(res)).GetProperty("apiToken").GetProperty("id").GetString();

        var created = await _events.WaitAsync("token:created");
        Assert.Equal(tokenId, Str(created, "tokenId"));
        Assert.Equal("evt-token", Str(created, "name"));
        var owner = Str(created, "userId");
        Assert.NotNull(owner);
        Assert.Equal(new CliEventAudience("tokens:read", owner), created.Audience);

        Assert.Equal(204, (int)(await _h.DeleteAsync($"tokens/{tokenId}", _root)).StatusCode);
        var revoked = await _events.WaitAsync("token:revoked");
        Assert.Equal(tokenId, Str(revoked, "tokenId"));
    }
}

public class ServerEventsFilesAndCommandsTests
{
    [Fact]
    public async Task FilesApi_PublishesFsChanged_ForUploadMkdirDelete()
    {
        await using var h = await McpPlainHost.StartAsync(cli =>
        {
            cli.AddFileSystem(o => o.UseInMemory());
            cli.AddMcp();
        });
        using var recorder = new EventRecorder(h.Host.Services.GetRequiredService<ICliEventBus>());

        var mkdir = await h.Client.PostAsync("/api/qcli/fs/mkdir",
            new StringContent("{\"path\":\"/docs\"}", Encoding.UTF8, "application/json"));
        Assert.True(mkdir.IsSuccessStatusCode, $"mkdir: {(int)mkdir.StatusCode}");

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent("hello"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "a.txt");
        form.Add(new StringContent("/docs/a.txt"), "path");
        var upload = await h.Client.PostAsync("/api/qcli/fs/upload", form);
        Assert.True(upload.IsSuccessStatusCode, $"upload: {(int)upload.StatusCode}");

        Assert.False((await Mcp.CallAsync(h.Client, "fs_write", new { path = "/docs/b.txt", content = "x" })).GetProperty("isError").GetBoolean());

        var rm = await h.Client.DeleteAsync("/api/qcli/fs/rm?path=/docs/a.txt");
        Assert.True(rm.IsSuccessStatusCode, $"rm: {(int)rm.StatusCode}");

        var changes = recorder.OfType(CliEventFileStorageProvider.ChangedEvent);
        Assert.Equal(["mkdir", "upload", "write", "delete"], changes.Select(e => (string)e.Data["action"]!));
        Assert.Equal("/docs", changes[0].Data["path"]);
        Assert.Equal("/docs/a.txt", changes[1].Data["path"]);
        Assert.Equal("/docs/b.txt", changes[2].Data["path"]);
        Assert.All(changes, e => Assert.Equal(CliEventAudience.WithPermission("files:read"), e.Audience));
    }

    [Fact]
    public async Task FileProvider_MoveAndCopy_CarryNewPath_FailuresPublishNothing()
    {
        var bus = new CliEventBus();
        using var recorder = new EventRecorder(bus);
        var provider = new CliEventFileStorageProvider(new InMemoryFileStorageProvider(), bus);

        await provider.WriteFileAsync("/a.txt", "x");
        await provider.CopyAsync("/a.txt", "/b.txt");
        await provider.MoveAsync("/b.txt", "/c.txt");
        await Assert.ThrowsAnyAsync<Exception>(() => provider.MoveAsync("/missing.txt", "/d.txt"));

        var changes = recorder.OfType("fs:changed");
        Assert.Equal(["write", "copy", "move"], changes.Select(e => (string)e.Data["action"]!));
        Assert.Equal("/a.txt", changes[1].Data["path"]);
        Assert.Equal("/b.txt", changes[1].Data["newPath"]);
        Assert.Equal("/b.txt", changes[2].Data["path"]);
        Assert.Equal("/c.txt", changes[2].Data["newPath"]);
    }

    [Fact]
    public async Task Commands_PublishExecuted_WithTheProcessorNameOnly()
    {
        await using var h = await McpPlainHost.StartAsync(cli =>
        {
            cli.AddProcessor(new TestProcessor("greet", "says hello"));
            cli.AddMcp();
        });
        using var recorder = new EventRecorder(h.Host.Services.GetRequiredService<ICliEventBus>(), "command:*");

        var body = new StringContent("{\"command\":\"greet\",\"rawCommand\":\"greet secret-value --token abc\",\"value\":\"secret-value\",\"args\":{\"token\":\"abc\"}}",
            Encoding.UTF8, "application/json");
        Assert.True((await h.Client.PostAsync("/api/v1/qcli/execute", body)).IsSuccessStatusCode);

        var unknown = new StringContent("{\"command\":\"not-a-command\"}", Encoding.UTF8, "application/json");
        await h.Client.PostAsync("/api/v1/qcli/execute", unknown);

        var stream = new StringContent("{\"command\":\"greet\"}", Encoding.UTF8, "application/json");
        await (await h.Client.PostAsync("/api/v1/qcli/execute/stream", stream)).Content.ReadAsStringAsync();

        var executed = recorder.OfType("command:executed");
        Assert.Equal(2, executed.Count); // unknown commands are not published
        var e = executed[0];
        Assert.Equal("greet", e.Data["command"]);
        Assert.Equal(true, e.Data["success"]);
        Assert.IsType<long>(e.Data["durationMs"]);
        Assert.Equal(CliEventAudience.WithPermission("audit:read"), e.Audience);
        var wire = CliEventBus.Serialize(e);
        Assert.DoesNotContain("secret-value", wire);
        Assert.DoesNotContain("abc", wire);
    }

    [Fact]
    public async Task Commands_OnAProtectedServer_CarryTheUserId()
    {
        await using var h = await UsersTestHost.StartAsync(configureCli: cli => cli.AddProcessor(new TestProcessor("greet", "hi")));
        using var recorder = new EventRecorder(h.Service<ICliEventBus>(), "command:*");
        var root = await h.RootTokenAsync();

        Assert.True((await h.PostAsync("execute", root, new { command = "greet" })).IsSuccessStatusCode);

        var e = Assert.Single(recorder.Events);
        Assert.NotNull(e.Data["userId"]);
        Assert.Equal("root", e.Data["actor"]);
    }

    [Fact]
    public async Task AuditStore_PublishesRecorded_ForTheHostsOwnEntries()
    {
        await using var h = await McpPlainHost.StartAsync(cli => cli.SetAuditStore(new InMemoryAuditStore()));
        using var recorder = new EventRecorder(h.Host.Services.GetRequiredService<ICliEventBus>());

        await h.Host.Services.GetRequiredService<IAuditStore>().LogAsync(new AuditEntry
        {
            Id = "a1",
            Timestamp = 1_762_424_100_000L,
            Event = "plan_changed",
            Username = "alice",
            Properties = new() { ["from"] = "free", ["to"] = "pro" },
        });

        var e = Assert.Single(recorder.OfType("audit:recorded"));
        Assert.Equal("a1", e.Data["id"]);
        Assert.Equal("plan_changed", e.Data["event"]);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_762_424_100_000L).UtcDateTime, e.Timestamp);
        var json = JsonSerializer.Deserialize<JsonElement>(CliEventBus.Serialize(e));
        Assert.Equal("pro", json.GetProperty("properties").GetProperty("to").GetString());
        Assert.Equal("2025-11-06T10:15:00.000Z", json.GetProperty("timestamp").GetString());
    }
}
