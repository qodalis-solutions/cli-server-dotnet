using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Tests.Helpers;
using static Qodalis.Cli.Tests.Helpers.UsersTestHost;

namespace Qodalis.Cli.Tests;

/// <summary>
/// <see cref="IExternalAuthAdapter.AuthenticateAsync"/>: async delegates and adapters, the sync
/// default, and the async adapter on the re-check of an open shell.
/// </summary>
public class ExternalAuthAsyncAdapterTests
{
    /// <summary>What the host's "database" says about each user (changes during a test).</summary>
    public sealed class HostUsers
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, bool> Admins { get; } = new();
        public int Calls;

        public async ValueTask<ExternalAuthResult?> LoadAsync(HttpContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await Task.Yield(); // a real database round trip
            cancellationToken.ThrowIfCancellationRequested();
            var name = context.Request.Headers["X-Test-User"].FirstOrDefault();
            if (string.IsNullOrEmpty(name) || !Admins.TryGetValue(name, out var admin)) return null;
            return new ExternalAuthResult
            {
                User = new User { Id = $"ext-{name}", Name = name, Email = $"{name}@external.com" },
                IsAdmin = admin,
            };
        }
    }

    /// <summary>Implements only the async method, with a DI dependency.</summary>
    private sealed class AsyncOnlyAdapter(HostUsers users) : IExternalAuthAdapter
    {
        public ValueTask<ExternalAuthResult?> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken) =>
            users.LoadAsync(context, cancellationToken);
    }

    /// <summary>Implements only the sync method (pre-existing adapters keep working).</summary>
    private sealed class SyncOnlyAdapter : IExternalAuthAdapter
    {
        public int Calls;

        public ExternalAuthResult? Authenticate(HttpContext context)
        {
            Interlocked.Increment(ref Calls);
            var name = context.Request.Headers["X-Test-User"].FirstOrDefault();
            return string.IsNullOrEmpty(name)
                ? null
                : new ExternalAuthResult { User = new User { Id = $"ext-{name}", Name = name, Email = $"{name}@external.com" } };
        }
    }

    private static async Task<string?> CurrentUserAsync(UsersTestHost h, string? user)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/sessions/current");
        if (user != null) req.Headers.Add("X-Test-User", user);
        var res = await h.Client.SendAsync(req);
        if ((int)res.StatusCode == 401) return null;
        Assert.Equal(200, (int)res.StatusCode);
        return (await ReadJsonAsync(res)).GetProperty("user").GetProperty("name").GetString();
    }

    [Fact]
    public async Task AsyncDelegate_AuthenticatesRequests()
    {
        var users = new HostUsers();
        users.Admins["alice"] = true;
        await using var h = await UsersTestHost.StartAsync(o => o.UseExternalAuth(users.LoadAsync));

        Assert.Equal("alice", await CurrentUserAsync(h, "alice"));
        Assert.Null(await CurrentUserAsync(h, "mallory"));
        Assert.Null(await CurrentUserAsync(h, null));
        Assert.True(users.Calls >= 3);
    }

    [Fact]
    public async Task AsyncLambda_IsAccepted()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.UseExternalAuth(async (context, ct) =>
        {
            await Task.Delay(1, ct);
            return new ExternalAuthResult { User = new User { Id = "ext-x", Name = "x", Email = "x@external.com" } };
        }));

        Assert.Equal("x", await CurrentUserAsync(h, null));
    }

    [Fact]
    public async Task AsyncOnlyAdapter_FromDependencyInjection_AuthenticatesRequests()
    {
        var users = new HostUsers();
        users.Admins["bob"] = false;
        await using var h = await UsersTestHost.StartAsync(o => o.UseExternalAuth<AsyncOnlyAdapter>(),
            configureServices: s => s.AddSingleton(users));

        Assert.Equal("bob", await CurrentUserAsync(h, "bob"));
        Assert.Null(await CurrentUserAsync(h, "eve"));
    }

    [Fact]
    public async Task SyncOnlyAdapter_IsCalledThroughTheDefaultAsyncMethod()
    {
        var adapter = new SyncOnlyAdapter();
        await using var h = await UsersTestHost.StartAsync(o => o.UseExternalAuth(adapter));

        Assert.Equal("carol", await CurrentUserAsync(h, "carol"));
        Assert.True(adapter.Calls > 0);

        var result = await ((IExternalAuthAdapter)adapter).AuthenticateAsync(new DefaultHttpContext(), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public void AdapterImplementingNeither_ThrowsNotSupported()
    {
        IExternalAuthAdapter adapter = new NoMethodsAdapter();
        Assert.Throws<NotSupportedException>(() =>
            adapter.AuthenticateAsync(new DefaultHttpContext(), CancellationToken.None).AsTask().GetAwaiter().GetResult());
    }

    private sealed class NoMethodsAdapter : IExternalAuthAdapter;

    [Fact]
    public async Task DelegateAdapter_SyncAndAsyncForms_ReturnTheSameResult()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Test-User"] = "dave";
        var users = new HostUsers();
        users.Admins["dave"] = true;

        var asyncAdapter = new DelegateAuthAdapter(users.LoadAsync);
        Assert.Equal("dave", (await asyncAdapter.AuthenticateAsync(context, CancellationToken.None))!.User.Name);
        Assert.Equal("dave", asyncAdapter.Authenticate(context)!.User.Name);

        var syncAdapter = new DelegateAuthAdapter(c => new ExternalAuthResult
        {
            User = new User { Id = "s", Name = c.Request.Headers["X-Test-User"].ToString(), Email = "s@external.com" },
        });
        Assert.Equal("dave", (await syncAdapter.AuthenticateAsync(context, CancellationToken.None))!.User.Name);
    }

    [Fact]
    public async Task AsyncAdapter_RecheckOfAnOpenShell_ClosesWhenTheUserIsGone()
    {
        var users = new HostUsers();
        users.Admins["alice"] = true;
        await using var h = await UsersTestHost.StartAsync(o => o.UseExternalAuth(users.LoadAsync),
            configureCli: cli => cli.EnableShell(o =>
            {
                o.Shell = "/bin/sh";
                o.IdleTimeout = null;
                o.SessionCheckInterval = TimeSpan.FromMilliseconds(200);
            }));

        var socket = await ShellTestClient.ConnectAsync(h.Host, "/ws/v1/qcli/shell", r => r.Headers["X-Test-User"] = "alice");
        await ShellTestClient.ExpectReadyAsync(socket);
        var before = Volatile.Read(ref users.Calls);
        await Task.Delay(600);
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, socket.State);
        Assert.True(Volatile.Read(ref users.Calls) > before, "the re-check should call the async adapter");

        users.Admins.TryRemove("alice", out _);
        var close = await ShellTestClient.ReadUntilCloseAsync(socket, 10000);
        Assert.Equal(4401, close.Code);
        Assert.Equal("session ended", close.Reason);
    }
}
