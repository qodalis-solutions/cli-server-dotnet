using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Tests.Helpers;
using static Qodalis.Cli.Tests.Helpers.UsersTestHost;

namespace Qodalis.Cli.Tests;

/// <summary>
/// Integration tests for the external auth adapter mode. A custom header (X-Test-User)
/// simulates host-app authentication; X-Test-Admin and X-Test-Groups shape the user.
/// </summary>
public class ExternalAuthAdapterTests : IAsyncLifetime
{
    private const string ExternalMessage = "Authentication is managed by the host application";
    private const string AppOrigin = "http://app.test";
    private UsersTestHost _h = null!;

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync(options =>
        {
            options.UseExternalAuth(context =>
            {
                var userHeader = context.Request.Headers["X-Test-User"].FirstOrDefault();
                if (string.IsNullOrEmpty(userHeader))
                    return null;

                var isAdmin = context.Request.Headers["X-Test-Admin"].FirstOrDefault() == "true";
                var groups = context.Request.Headers["X-Test-Groups"].FirstOrDefault()?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [];

                return new ExternalAuthResult
                {
                    User = new User
                    {
                        Id = $"ext-{userHeader}",
                        Name = userHeader,
                        Email = $"{userHeader}@external.com",
                        Groups = groups,
                    },
                    IsAdmin = isAdmin,
                };
            });
        }, configureCli: cli => cli.SetAllowedOrigins(AppOrigin));
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? user = null,
        bool admin = false, string? groups = null, object? body = null, string? origin = AppOrigin)
    {
        var req = new HttpRequestMessage(method, $"{Base}/{path}");
        if (origin != null && method != HttpMethod.Get) req.Headers.Add("Origin", origin);
        if (user != null)
        {
            req.Headers.Add("X-Test-User", user);
            if (admin) req.Headers.Add("X-Test-Admin", "true");
            if (groups != null) req.Headers.Add("X-Test-Groups", groups);
        }
        if (body != null) req.Content = System.Net.Http.Json.JsonContent.Create(body);
        return _h.Client.SendAsync(req);
    }

    // =========================================================================
    // Credential endpoints answer 501
    // =========================================================================

    [Fact]
    public async Task Login_Returns501()
    {
        var res = await SendAsync(HttpMethod.Post, "sessions", body: new { username = "root", password = "root" });
        Assert.Equal(ExternalMessage, await AssertErrorAsync(res, 501));
    }

    [Fact]
    public async Task Logout_Returns501()
    {
        var res = await SendAsync(HttpMethod.Delete, "sessions/current", "admin-user", admin: true);
        Assert.Equal(ExternalMessage, await AssertErrorAsync(res, 501));
    }

    [Fact]
    public async Task VerifyPassword_Returns501()
    {
        var res = await SendAsync(HttpMethod.Post, "users/some-id/verify-password", "admin-user", admin: true,
            body: new { password = "some-pass" });
        Assert.Equal(ExternalMessage, await AssertErrorAsync(res, 501));
    }

    [Fact]
    public async Task ChangePassword_Returns501()
    {
        var res = await SendAsync(HttpMethod.Put, "users/some-id/password", "admin-user", admin: true,
            body: new { password = "some-pass" });
        Assert.Equal(ExternalMessage, await AssertErrorAsync(res, 501));
    }

    // =========================================================================
    // Seeding
    // =========================================================================

    [Fact]
    public async Task BuiltInGroups_AreSeeded_RootIsNot()
    {
        var groups = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "groups", "admin-user", admin: true));
        var names = groups.EnumerateArray().Select(g => g.GetProperty("name").GetString()).ToList();
        Assert.Contains("admin", names);
        Assert.Contains("users", names);

        var users = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "users", "admin-user", admin: true));
        Assert.Equal(0, users.GetArrayLength());
    }

    // =========================================================================
    // Adapter authenticates requests; permissions come from groups
    // =========================================================================

    [Fact]
    public async Task GetUsers_WithoutAuthHeader_Returns401()
    {
        await AssertErrorAsync(await SendAsync(HttpMethod.Get, "users"), 401);
    }

    [Fact]
    public async Task AdminAdapterUser_HasAllPermissions()
    {
        Assert.Equal(200, (int)(await SendAsync(HttpMethod.Get, "users", "alice", admin: true)).StatusCode);

        var created = await SendAsync(HttpMethod.Post, "users", "alice", admin: true,
            body: new { name = "ext-new-user", email = "ext@test.com" });
        Assert.Equal(201, (int)created.StatusCode);
        Assert.Equal("ext-new-user", (await ReadJsonAsync(created)).GetProperty("name").GetString());

        var current = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "sessions/current", "alice", admin: true));
        Assert.Equal("alice", current.GetProperty("user").GetProperty("name").GetString());
        Assert.Contains("admin", current.GetProperty("user").GetProperty("groups").EnumerateArray().Select(g => g.GetString()));
        Assert.Equal("*", current.GetProperty("permissions")[0].GetString());
    }

    [Fact]
    public async Task NonAdminAdapterUser_IsDeniedAdminEndpoints()
    {
        await AssertErrorAsync(await SendAsync(HttpMethod.Get, "users", "regular-user"), 403);
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "users", "regular-user",
            body: new { name = "should-fail", email = "fail@test.com" }), 403);
        await AssertErrorAsync(await SendAsync(HttpMethod.Get, "audit", "regular-user"), 403);
        Assert.Equal(200, (int)(await SendAsync(HttpMethod.Get, "groups", "regular-user")).StatusCode);

        var current = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "sessions/current", "bob"));
        Assert.Equal("bob", current.GetProperty("user").GetProperty("name").GetString());
        Assert.Equal(0, current.GetProperty("permissions").GetArrayLength());
    }

    [Fact]
    public async Task AdapterUser_InCustomGroup_GetsGroupPermissions()
    {
        var create = await SendAsync(HttpMethod.Post, "groups", "alice", admin: true,
            body: new { name = "auditors", permissions = new[] { "audit:read" } });
        Assert.Equal(201, (int)create.StatusCode);

        Assert.Equal(200, (int)(await SendAsync(HttpMethod.Get, "audit", "carol", groups: "AUDITORS")).StatusCode);
        await AssertErrorAsync(await SendAsync(HttpMethod.Delete, "audit", "carol", groups: "auditors"), 403);
    }

    [Fact]
    public async Task AdapterSessions_AreNotListed()
    {
        await SendAsync(HttpMethod.Get, "sessions/current", "bob");
        var res = await SendAsync(HttpMethod.Get, "sessions", "admin-user", admin: true);
        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal(0, (await ReadJsonAsync(res)).GetArrayLength());
    }

    // =========================================================================
    // CSRF (B9): cookie/adapter-authenticated state changes need an allowed Origin
    // =========================================================================

    [Fact]
    public async Task StateChange_WithoutAllowedOrigin_Returns403()
    {
        var body = new { name = "csrf-user", email = "csrf@test.com" };
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "users", "alice", admin: true, body: body, origin: null), 403);
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "users", "alice", admin: true, body: body, origin: "http://evil.test"), 403);
        await AssertErrorAsync(await SendAsync(HttpMethod.Delete, "audit", "alice", admin: true, origin: null), 403);

        Assert.Equal(201, (int)(await SendAsync(HttpMethod.Post, "users", "alice", admin: true, body: body)).StatusCode);
        // Reads are not subject to the Origin requirement.
        Assert.Equal(200, (int)(await SendAsync(HttpMethod.Get, "users", "alice", admin: true, origin: null)).StatusCode);
    }

    [Fact]
    public async Task AdapterMode_ProtectsServerRoutes()
    {
        await AssertErrorAsync(await SendAsync(HttpMethod.Get, "commands"), 401);
        Assert.Equal(200, (int)(await SendAsync(HttpMethod.Get, "commands", "bob")).StatusCode);
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "execute", "bob", body: new { command = "x" }), 403);
    }

    // =========================================================================
    // Cross-site WebSocket hijacking: adapter (cookie) auth on upgrades needs an allowed Origin
    // =========================================================================

    private static Task<UsersTestHost> StartWithoutAllowedOriginsAsync() =>
        UsersTestHost.StartAsync(options => options.UseExternalAuth(context =>
        {
            var user = context.Request.Headers["X-Test-User"].FirstOrDefault();
            return string.IsNullOrEmpty(user)
                ? null
                : new ExternalAuthResult
                {
                    User = new User { Id = $"ext-{user}", Name = user, Email = $"{user}@external.com" },
                    IsAdmin = true,
                };
        }), configureCli: cli => cli.EnableShell(o =>
        {
            o.Shell = "/bin/sh";
            o.IdleTimeout = null;
        }));

    private static Action<Microsoft.AspNetCore.Http.HttpRequest> AdapterUser(string? origin) => r =>
    {
        r.Headers["X-Test-User"] = "alice";
        if (origin != null) r.Headers.Origin = origin;
    };

    [Fact]
    public async Task AdapterAuthenticatedWsUpgrade_FromForeignOrigin_IsRejected()
    {
        await using var h = await StartWithoutAllowedOriginsAsync();

        // Events socket: HTTP 403 for a cross-site upgrade that relies on adapter (cookie) auth.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ShellTestClient.ConnectAsync(h.Host, "/ws/v1/qcli/events", AdapterUser("http://evil.test")));
        Assert.Contains("403", ex.Message);

        // Shell socket: accepted then closed with 4403, never opens a shell.
        var shell = await ShellTestClient.CloseOfAsync(h.Host, "/ws/v1/qcli/shell?cmd=echo%20pwned",
            AdapterUser("http://evil.test"));
        Assert.Equal(4403, shell.Code);
        Assert.False(shell.GotReady);
        Assert.DoesNotContain("pwned", shell.Stdout);
    }

    [Fact]
    public async Task AdapterAuthenticatedWsUpgrade_SameOriginOrNoOrigin_IsAccepted()
    {
        await using var h = await StartWithoutAllowedOriginsAsync();

        using (var noOrigin = await ShellTestClient.ConnectAsync(h.Host, "/ws/v1/qcli/events", AdapterUser(null)))
            await CloseAsync(noOrigin);
        using (var sameOrigin = await ShellTestClient.ConnectAsync(h.Host, "/ws/v1/qcli/events", AdapterUser("http://localhost")))
            await CloseAsync(sameOrigin);

        var shell = await ShellTestClient.CloseOfAsync(h.Host, "/ws/v1/qcli/shell?cmd=echo%20same-ok",
            AdapterUser("http://localhost"));
        Assert.True(shell.GotReady);
        Assert.Contains("same-ok", shell.Stdout);
    }

    // =========================================================================
    // Shell session binding (C2): adapter principals are re-validated through the adapter
    // =========================================================================

    /// <summary>What the host application currently says about each user (changes during a test).</summary>
    private sealed class HostAuthState
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, string> Users { get; } = new();
        public int Calls;
    }

    private static Task<UsersTestHost> StartShellHostAsync(HostAuthState state) =>
        UsersTestHost.StartAsync(options => options.UseExternalAuth(context =>
        {
            Interlocked.Increment(ref state.Calls);
            var user = context.Request.Headers["X-Test-User"].FirstOrDefault();
            if (string.IsNullOrEmpty(user) || !state.Users.TryGetValue(user, out var status) || status == "gone")
                return null;
            if (status == "throw") throw new InvalidOperationException("host auth unavailable");
            return new ExternalAuthResult
            {
                User = new User { Id = $"ext-{user}", Name = user, Email = $"{user}@external.com" },
                IsAdmin = status == "admin", // admin: '*' (shell:execute); otherwise no permissions
            };
        }), configureCli: cli => cli.EnableShell(o =>
        {
            o.Shell = "/bin/sh";
            o.IdleTimeout = null;
            o.SessionCheckInterval = TimeSpan.FromMilliseconds(200);
        }));

    private static async Task<System.Net.WebSockets.WebSocket> OpenAdapterShellAsync(UsersTestHost h, string user)
    {
        var socket = await ShellTestClient.ConnectAsync(h.Host, "/ws/v1/qcli/shell", r => r.Headers["X-Test-User"] = user);
        await ShellTestClient.ExpectReadyAsync(socket);
        return socket;
    }

    [Fact]
    public async Task AdapterShell_StaysOpenAcrossPeriodicRechecks()
    {
        var state = new HostAuthState();
        state.Users["alice"] = "admin";
        await using var h = await StartShellHostAsync(state);
        var socket = await OpenAdapterShellAsync(h, "alice");

        var before = Volatile.Read(ref state.Calls);
        await Task.Delay(1000); // several re-checks
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, socket.State);
        Assert.True(Volatile.Read(ref state.Calls) > before, "the adapter should be consulted by the re-checks");
        socket.Abort();
    }

    [Theory]
    [InlineData("gone", "session ended")]
    [InlineData("throw", "session ended")]
    [InlineData("user", "permission removed")]
    public async Task AdapterShell_ClosesWhenTheHostNoLongerAuthenticates(string status, string auditReason)
    {
        var state = new HostAuthState();
        state.Users["alice"] = "admin";
        state.Users["auditor"] = "admin";
        await using var h = await StartShellHostAsync(state);
        var socket = await OpenAdapterShellAsync(h, "alice");

        state.Users["alice"] = status;
        var close = await ShellTestClient.ReadUntilCloseAsync(socket, 10000);
        Assert.Equal(4401, close.Code);
        Assert.Equal("session ended", close.Reason);

        JsonElement closed = default;
        await ShellSecurityTests.WaitUntilAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/audit?event=shell_closed");
            req.Headers.Add("X-Test-User", "auditor");
            var list = ReadJsonAsync(h.Client.SendAsync(req).Result).Result;
            if (list.GetArrayLength() == 0) return false;
            closed = list[0];
            return true;
        });
        Assert.Equal(auditReason, closed.GetProperty("metadata").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AdapterShell_OpenedWithTicket_IsRevalidatedWithTheTicketRequest()
    {
        var state = new HostAuthState();
        state.Users["alice"] = "admin";
        await using var h = await StartShellHostAsync(state);

        var ticketRequest = new HttpRequestMessage(HttpMethod.Post, $"{Base}/sessions/ws-ticket");
        ticketRequest.Headers.Add("X-Test-User", "alice");
        ticketRequest.Headers.Add("Origin", "http://localhost"); // same-origin (CSRF check)
        var ticket = (await ReadJsonAsync(await h.Client.SendAsync(ticketRequest))).GetProperty("ticket").GetString();

        // The upgrade itself carries no host credentials (only the ticket).
        var socket = await ShellTestClient.ConnectAsync(h.Host, $"/ws/v1/qcli/shell?ticket={ticket}");
        await ShellTestClient.ExpectReadyAsync(socket);
        await Task.Delay(600);
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, socket.State);

        state.Users["alice"] = "gone";
        Assert.Equal(4401, (await ShellTestClient.ReadUntilCloseAsync(socket, 10000)).Code);
    }

    /// <summary>Host authentication (ASP.NET Core scheme) the adapter reads through <c>HttpContext.User</c>.</summary>
    private sealed class HostHeaderAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder, HostAuthState state)
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(
            options, logger, encoder)
    {
        protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Host-User"].FirstOrDefault();
            if (string.IsNullOrEmpty(user) || !state.Users.TryGetValue(user, out var status) || status == "gone")
                return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
            var identity = new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, user)], "Host");
            return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(
                new Microsoft.AspNetCore.Authentication.AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(identity), "Host")));
        }
    }

    [Fact]
    public async Task AdapterShell_UsingHostAuthentication_IsRevalidatedThroughTheScheme()
    {
        var state = new HostAuthState();
        state.Users["alice"] = "admin";
        await using var h = await UsersTestHost.StartAsync(
            options => options.UseExternalAuth(context =>
            {
                var name = context.User.Identity?.IsAuthenticated == true ? context.User.Identity.Name : null;
                return name == null
                    ? null
                    : new ExternalAuthResult
                    {
                        User = new User { Id = $"ext-{name}", Name = name, Email = $"{name}@external.com" },
                        IsAdmin = true,
                    };
            }),
            configureServices: services =>
            {
                services.AddSingleton(state);
                services.AddAuthentication("Host")
                    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, HostHeaderAuthHandler>("Host", _ => { });
            },
            configureCli: cli => cli.EnableShell(o =>
            {
                o.Shell = "/bin/sh";
                o.IdleTimeout = null;
                o.SessionCheckInterval = TimeSpan.FromMilliseconds(200);
            }),
            configureAuth: app => app.UseAuthentication());

        var socket = await ShellTestClient.ConnectAsync(h.Host, "/ws/v1/qcli/shell", r => r.Headers["X-Host-User"] = "alice");
        await ShellTestClient.ExpectReadyAsync(socket);
        await Task.Delay(600);
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, socket.State);

        state.Users["alice"] = "gone";
        Assert.Equal(4401, (await ShellTestClient.ReadUntilCloseAsync(socket, 10000)).Code);
    }

    [Fact]
    public async Task NonProtectedRoute_PassesThrough()
    {
        var res = await _h.Client.GetAsync("/api/v1/qcli/version");
        Assert.NotEqual(401, (int)res.StatusCode);
    }
}
