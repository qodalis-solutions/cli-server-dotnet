using System.Collections.Concurrent;
using System.Net.WebSockets;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Tests.Helpers;
using static Qodalis.Cli.Tests.Helpers.UsersTestHost;

namespace Qodalis.Cli.Tests;

/// <summary>
/// Events and logs sockets opened through an external adapter are re-checked by running the adapter
/// again on the request snapshot every <c>SessionCheckInterval</c> (issue #25).
/// </summary>
public class ExternalAuthSocketRecheckTests
{
    /// <summary>
    /// What the host says about each user: <c>admin</c> (<c>*</c>), <c>user</c> (no permissions),
    /// <c>gone</c> (null), <c>other</c> (another user), <c>disabled</c>, <c>throw</c>.
    /// </summary>
    private sealed class HostState
    {
        public ConcurrentDictionary<string, string> Users { get; } = new();
        public int Calls;
    }

    private static Task<UsersTestHost> StartAsync(HostState state, TimeSpan? checkInterval = null) =>
        UsersTestHost.StartAsync(options => options.UseExternalAuth(async (context, ct) =>
            {
                Interlocked.Increment(ref state.Calls);
                await Task.Yield();
                var name = context.Request.Headers["X-Test-User"].FirstOrDefault();
                if (string.IsNullOrEmpty(name) || !state.Users.TryGetValue(name, out var status) || status == "gone")
                    return null;
                if (status == "throw") throw new InvalidOperationException("host auth unavailable");
                var id = status == "other" ? "ext-someone-else" : $"ext-{name}";
                return new ExternalAuthResult
                {
                    User = new User { Id = id, Name = name, Email = $"{name}@external.com", Disabled = status == "disabled" },
                    IsAdmin = status is "admin" or "other" or "disabled",
                };
            }),
            realEventSockets: true,
            configureCli: cli => cli.ServerOptions.Shell.SessionCheckInterval = checkInterval ?? TimeSpan.FromMilliseconds(150));

    private static async Task<WebSocket> OpenAsync(UsersTestHost h, string path, string user)
    {
        var socket = await ShellTestClient.ConnectAsync(h.Host, path, r => r.Headers["X-Test-User"] = user);
        Assert.Contains("connected", await ReceiveTextAsync(socket));
        return socket;
    }

    [Theory]
    [InlineData("/ws/v1/qcli/events", "gone")]
    [InlineData("/ws/v1/qcli/events", "other")]
    [InlineData("/ws/v1/qcli/events", "disabled")]
    [InlineData("/ws/v1/qcli/events", "throw")]
    [InlineData("/ws/v1/qcli/logs", "gone")]
    [InlineData("/ws/v1/qcli/logs", "other")]
    [InlineData("/ws/v1/qcli/logs", "disabled")]
    [InlineData("/ws/v1/qcli/logs", "throw")]
    [InlineData("/ws/v1/qcli/logs", "user")] // no longer holds logs:read
    public async Task Socket_ClosesWhenTheHostNoLongerAuthenticates(string path, string status)
    {
        var state = new HostState();
        state.Users["alice"] = "admin";
        await using var h = await StartAsync(state);
        var socket = await OpenAsync(h, path, "alice");

        state.Users["alice"] = status;
        var close = await ShellTestClient.ReadUntilCloseAsync(socket, 10000);
        Assert.Equal(4401, close.Code);
        Assert.Equal("session ended", close.Reason);
    }

    [Fact]
    public async Task EventsSocket_WithoutPermissions_StaysOpen()
    {
        var state = new HostState();
        state.Users["bob"] = "admin";
        await using var h = await StartAsync(state);
        var socket = await OpenAsync(h, "/ws/v1/qcli/events", "bob");

        // Events need no permission: losing every permission keeps the socket.
        state.Users["bob"] = "user";
        var before = Volatile.Read(ref state.Calls);
        await Task.Delay(700);
        Assert.True(Volatile.Read(ref state.Calls) > before, "the adapter should be consulted by the re-checks");

        await h.Service<Qodalis.Cli.Services.ICliEventSocketManager>().BroadcastMessageAsync("{\"type\":\"marker\"}");
        Assert.Contains("marker", await ReceiveTextAsync(socket));
        await CloseAsync(socket);
    }

    [Fact]
    public async Task LogsSocket_StaysOpenAcrossRechecksWhileAuthenticated()
    {
        var state = new HostState();
        state.Users["carol"] = "admin";
        await using var h = await StartAsync(state);
        var socket = await OpenAsync(h, "/ws/v1/qcli/logs", "carol");

        var before = Volatile.Read(ref state.Calls);
        var close = await ShellTestClient.ReadUntilCloseAsync(socket, 800); // several re-checks, no close
        Assert.Null(close.Code);
        Assert.True(Volatile.Read(ref state.Calls) > before);
    }

    [Fact]
    public async Task SocketOpenedWithATicket_IsRecheckedWithTheTicketRequest()
    {
        var state = new HostState();
        state.Users["dave"] = "admin";
        await using var h = await StartAsync(state);

        var ticketRequest = new HttpRequestMessage(HttpMethod.Post, $"{Base}/sessions/ws-ticket");
        ticketRequest.Headers.Add("X-Test-User", "dave");
        ticketRequest.Headers.Add("Origin", "http://localhost");
        var ticket = (await ReadJsonAsync(await h.Client.SendAsync(ticketRequest))).GetProperty("ticket").GetString();

        var socket = await ShellTestClient.ConnectAsync(h.Host, $"/ws/v1/qcli/logs?ticket={ticket}");
        Assert.Contains("connected", await ReceiveTextAsync(socket));

        state.Users["dave"] = "gone";
        Assert.Equal(4401, (await ShellTestClient.ReadUntilCloseAsync(socket, 10000)).Code);
    }

    [Fact]
    public async Task OnlyTheAffectedUsersSocketsClose()
    {
        var state = new HostState();
        state.Users["erin"] = "admin";
        state.Users["frank"] = "admin";
        await using var h = await StartAsync(state);
        var erin = await OpenAsync(h, "/ws/v1/qcli/events", "erin");
        var frank = await OpenAsync(h, "/ws/v1/qcli/events", "frank");

        state.Users["erin"] = "gone";
        Assert.Equal(4401, (await ShellTestClient.ReadUntilCloseAsync(erin, 10000)).Code);

        await h.Service<Qodalis.Cli.Services.ICliEventSocketManager>().BroadcastMessageAsync("{\"type\":\"marker\"}");
        Assert.Contains("marker", await ReceiveTextAsync(frank));
        await CloseAsync(frank);
    }

    [Fact]
    public async Task DisabledUser_IsRefusedOnRequests()
    {
        var state = new HostState();
        state.Users["gina"] = "disabled";
        await using var h = await StartAsync(state);

        var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/sessions/current");
        req.Headers.Add("X-Test-User", "gina");
        Assert.Equal("account disabled", await AssertErrorAsync(await h.Client.SendAsync(req), 401));
    }

    [Fact]
    public void CheckInterval_FollowsSessionCheckInterval_CappedAt60s()
    {
        var options = new Qodalis.Cli.Security.CliServerOptions();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, options);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
            .BuildServiceProvider(services);
        var bootstrap = new Qodalis.Cli.Plugin.Users.Services.UsersBootstrapService(
            new Qodalis.Cli.Plugin.Users.Stores.InMemoryUserStore(),
            new Qodalis.Cli.Plugin.Users.Stores.InMemoryGroupStore(),
            null!, new Qodalis.Cli.Plugin.Users.UsersModuleOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Qodalis.Cli.Plugin.Users.Services.UsersBootstrapService>.Instance,
            provider);

        Assert.Equal(TimeSpan.FromSeconds(30), bootstrap.ExternalSocketCheckInterval);
        options.Shell.SessionCheckInterval = TimeSpan.FromSeconds(5);
        Assert.Equal(TimeSpan.FromSeconds(5), bootstrap.ExternalSocketCheckInterval);
        options.Shell.SessionCheckInterval = TimeSpan.FromMinutes(10);
        Assert.Equal(TimeSpan.FromSeconds(60), bootstrap.ExternalSocketCheckInterval);
    }

    [Fact]
    public async Task StopAsync_AfterDispose_DoesNothing()
    {
        // With minimal hosting the app's Run() can dispose the container while a test host still stops it.
        var bootstrap = new Qodalis.Cli.Plugin.Users.Services.UsersBootstrapService(
            new Qodalis.Cli.Plugin.Users.Stores.InMemoryUserStore(),
            new Qodalis.Cli.Plugin.Users.Stores.InMemoryGroupStore(),
            null!, new Qodalis.Cli.Plugin.Users.UsersModuleOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Qodalis.Cli.Plugin.Users.Services.UsersBootstrapService>.Instance);

        bootstrap.Dispose();
        await bootstrap.StopAsync(CancellationToken.None);
        bootstrap.Dispose();
    }
}
