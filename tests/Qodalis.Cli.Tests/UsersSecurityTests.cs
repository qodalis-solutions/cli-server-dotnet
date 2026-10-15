using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.Users.Authorization;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;
using static Qodalis.Cli.Tests.Helpers.UsersTestHost;

namespace Qodalis.Cli.Tests;

/// <summary>Host route protected with <see cref="QcliAuthorizeAttribute"/> (B1 reusable guard).</summary>
[ApiController]
[Route("host/reports")]
public class HostReportsController : ControllerBase
{
    [HttpGet]
    [QcliAuthorize("reports:read")]
    public IActionResult Get() => Ok(new { ok = true });

    [HttpGet("any")]
    [QcliAuthorize]
    public IActionResult Any() => Ok(new { ok = true });
}

/// <summary>
/// Security contract tests (web-cli spec "Appendix B — Security contract", B1–B10).
/// </summary>
public class UsersSecurityTests : IAsyncLifetime
{
    private const string AppOrigin = "http://localhost:4300";
    private UsersTestHost _h = null!;
    private string _root = string.Empty;

    private static void ConfigureCli(CliBuilder cli)
    {
        cli.SetAllowedOrigins(AppOrigin, "http://127.0.0.1:*");
        cli.AddProcessor(new TestProcessor("echo", "Echo"));
        cli.AddProcessor(new TestProcessor("secret-cmd", "Needs secret:run") { RequiredPermissions = ["secret:run"] });
        cli.AddProcessor(new TestProcessor("release", "Release")
        {
            Processors =
            [
                new TestProcessor("prod", "Needs release:prod")
                {
                    RequiredPermissions = ["release:prod"],
                    Processors = [new TestProcessor("run", "Run")],
                },
            ],
        });
    }

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync(configureCli: ConfigureCli, realEventSockets: true,
            configureMvc: mvc => mvc.AddApplicationPart(typeof(HostReportsController).Assembly));
        _root = await _h.RootTokenAsync();
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<string> UserAsync(string name, params string[] permissions) =>
        permissions.Length == 0
            ? CreatePlainUserAsync(name)
            : _h.UserWithPermissionsAsync(_root, name, permissions);

    private async Task<string> CreatePlainUserAsync(string name)
    {
        await _h.CreateUserAsync(_root, name);
        return (await _h.LoginAsync(name, "pass1234")).Token;
    }

    private static object Exec(string command) => new { command, value = "", args = new { }, chainCommands = Array.Empty<string>() };

    // =========================================================================
    // B1 — the users plugin protects the whole server
    // =========================================================================

    [Theory]
    [InlineData("GET", "/api/v1/qcli/commands")]
    [InlineData("POST", "/api/v1/qcli/execute")]
    [InlineData("POST", "/api/v1/qcli/execute/stream")]
    [InlineData("GET", "/api/qcli/fs/ls?path=/")]
    [InlineData("GET", "/api/qcli/fs/cat?path=/x")]
    [InlineData("POST", "/api/qcli/fs/mkdir")]
    [InlineData("DELETE", "/api/qcli/fs/rm?path=/x")]
    [InlineData("GET", "/api/qcli/data-explorer/sources")]
    [InlineData("GET", "/api/v1/qcli/jobs")]
    [InlineData("GET", "/api/v1/qcli/anything-else")]
    public async Task AnonymousRequests_ToServerRoutes_Return401(string method, string path)
    {
        var res = await _h.SendAsync(new HttpMethod(method), path, null, method == "GET" || method == "DELETE" ? null : Exec("echo"));
        await AssertErrorAsync(res, 401);
    }

    [Theory]
    [InlineData("/api/qcli/versions")]
    [InlineData("/api/v1/qcli/version")]
    [InlineData("/api/v1/qcli/capabilities")]
    public async Task VersionDiscovery_IsPublic(string path)
    {
        Assert.Equal(200, (int)(await _h.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task PlainUser_CanUseCli_ButNotFilesDataOrJobs()
    {
        var token = await UserAsync("cli-user");

        Assert.Equal(200, (int)(await _h.GetAsync("commands", token)).StatusCode);
        var exec = await _h.PostAsync("execute", token, Exec("echo"));
        Assert.Equal(200, (int)exec.StatusCode);

        await AssertErrorAsync(await _h.GetAsync("/api/qcli/fs/ls?path=/", token), 403);
        await AssertErrorAsync(await _h.PostAsync("/api/qcli/fs/mkdir", token, new { path = "/x" }), 403);
        await AssertErrorAsync(await _h.GetAsync("/api/qcli/data-explorer/sources", token), 403);
        await AssertErrorAsync(await _h.GetAsync("/api/v1/qcli/jobs", token), 403);
        await AssertErrorAsync(await _h.GetAsync("/api/v1/qcli/status", token), 403);
    }

    [Fact]
    public async Task Execute_RequiresCommandsExecute()
    {
        var token = await UserAsync("no-exec", "users:read");
        Assert.Equal(200, (int)(await _h.GetAsync("commands", token)).StatusCode);
        await AssertErrorAsync(await _h.PostAsync("execute", token, Exec("echo")), 403);
        await AssertErrorAsync(await _h.PostAsync("execute/stream", token, Exec("echo")), 403);
    }

    [Fact]
    public async Task Execute_EnforcesProcessorRequiredPermissions()
    {
        var plain = await UserAsync("plain-exec");
        Assert.Contains("secret:run", await AssertErrorAsync(await _h.PostAsync("execute", plain, Exec("secret-cmd")), 403));
        await AssertErrorAsync(await _h.PostAsync("execute/stream", plain, Exec("secret-cmd")), 403);

        var runner = await UserAsync("secret-runner", "commands:execute", "secret:run");
        Assert.Equal(200, (int)(await _h.PostAsync("execute", runner, Exec("secret-cmd"))).StatusCode);
        Assert.Equal(200, (int)(await _h.PostAsync("execute", _root, Exec("secret-cmd"))).StatusCode);
    }

    [Fact]
    public async Task Execute_EnforcesPermissionsOfEveryProcessorInTheChain()
    {
        var execOnly = await UserAsync("release-exec", "commands:execute");
        var cmd = new { command = "release", value = "", args = new { }, chainCommands = new[] { "prod", "run" } };
        Assert.Contains("release:prod", await AssertErrorAsync(await _h.PostAsync("execute", execOnly, cmd), 403));

        var releaser = await UserAsync("releaser", "commands:execute", "release:prod");
        Assert.Equal(200, (int)(await _h.PostAsync("execute", releaser, cmd)).StatusCode);
    }

    [Fact]
    public async Task Files_ReadAndWritePermissions()
    {
        var reader = await UserAsync("file-reader", "files:read");
        Assert.Equal(200, (int)(await _h.GetAsync("/api/qcli/fs/ls?path=/", reader)).StatusCode);
        await AssertErrorAsync(await _h.PostAsync("/api/qcli/fs/mkdir", reader, new { path = "/made" }), 403);

        var writer = await UserAsync("file-writer", "files:*");
        Assert.NotEqual(403, (int)(await _h.PostAsync("/api/qcli/fs/mkdir", writer, new { path = "/made" })).StatusCode);
        Assert.NotEqual(403, (int)(await _h.GetAsync("/api/qcli/data-explorer/sources", await UserAsync("analyst", "data:query"))).StatusCode);
    }

    [Fact]
    public async Task ProtectServerFalse_LeavesServerRoutesOpen()
    {
        await using var host = await UsersTestHost.StartAsync(o => o.ProtectServer = false, configureCli: ConfigureCli);
        Assert.Equal(200, (int)(await host.GetAsync("commands")).StatusCode);
        Assert.Equal(200, (int)(await host.PostAsync("execute", null, Exec("secret-cmd"))).StatusCode);
        await AssertErrorAsync(await host.GetAsync("users"), 401);
    }

    [Fact]
    public async Task WithoutMiddleware_ServerRoutesFailClosed()
    {
        await using var host = await UsersTestHost.StartAsync(useMiddleware: false, configureCli: ConfigureCli);
        var (token, _) = await host.LoginAsync("root", "root");
        await AssertErrorAsync(await host.GetAsync("commands", token), 401);
        await AssertErrorAsync(await host.PostAsync("execute", token, Exec("echo")), 401);
        await AssertErrorAsync(await host.GetAsync("/api/qcli/fs/ls?path=/", token), 401);
        Assert.Equal(200, (int)(await host.GetAsync("/api/qcli/versions")).StatusCode);
    }

    [Fact]
    public async Task ServerWithoutUsersPlugin_BehavesAsBefore()
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(s => s.AddControllers().AddCli(cli => cli.AddProcessor(new TestProcessor("echo", "Echo"))));
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(e => e.MapControllers());
                app.UseCli();
            });
        });
        using var host = await builder.StartAsync();
        var client = host.GetTestClient();
        Assert.Equal(200, (int)(await client.GetAsync("/api/v1/qcli/commands")).StatusCode);
        Assert.Equal(200, (int)(await client.GetAsync("/api/qcli/fs/ls?path=/")).StatusCode);
        var exec = await client.PostAsync("/api/v1/qcli/execute",
            new StringContent(JsonSerializer.Serialize(Exec("echo")), Encoding.UTF8, "application/json"));
        Assert.Equal(200, (int)exec.StatusCode);
        await host.StopAsync();
    }

    [Fact]
    public async Task QcliAuthorize_ProtectsHostRoutes()
    {
        await AssertErrorAsync(await _h.GetAsync("/host/reports"), 401);
        await AssertErrorAsync(await _h.GetAsync("/host/reports", "bogus"), 401);
        await AssertErrorAsync(await _h.GetAsync("/host/reports", await UserAsync("no-reports")), 403);
        Assert.Equal(200, (int)(await _h.GetAsync("/host/reports", await UserAsync("reporter", "reports:read"))).StatusCode);
        Assert.Equal(200, (int)(await _h.GetAsync("/host/reports/any", await UserAsync("anyone"))).StatusCode);
        Assert.Equal(200, (int)(await _h.GetAsync("/host/reports", _root)).StatusCode);
    }

    [Fact]
    public async Task QcliAuthorizeEndpointFilter_ProtectsMinimalApis()
    {
        await using var host = await UsersTestHost.StartAsync(configureApp: app =>
            app.UseEndpoints(e => e.MapGet("/host/minimal", () => "ok").RequireQcliAuthorization("reports:read")));
        var root = await host.RootTokenAsync();
        await AssertErrorAsync(await host.GetAsync("/host/minimal"), 401);
        await host.CreateUserAsync(root, "minimal-user");
        var user = (await host.LoginAsync("minimal-user", "pass1234")).Token;
        await AssertErrorAsync(await host.GetAsync("/host/minimal", user), 403);
        Assert.Equal(200, (int)(await host.GetAsync("/host/minimal", root)).StatusCode);
    }

    [Fact]
    public void RoutePolicy_MapsSurfacesToPermissions()
    {
        Assert.Null(CliRoutePolicy.Resolve("GET", "/api/v1/qcli/commands").Permission);
        Assert.Equal("commands:execute", CliRoutePolicy.Resolve("POST", "/api/v1/qcli/execute/stream").Permission);
        Assert.Equal("files:read", CliRoutePolicy.Resolve("GET", "/api/qcli/fs/download").Permission);
        Assert.Equal("files:write", CliRoutePolicy.Resolve("POST", "/api/qcli/fs/upload").Permission);
        Assert.Equal("files:write", CliRoutePolicy.Resolve("DELETE", "/api/qcli/fs/rm").Permission);
        Assert.Equal("data:query", CliRoutePolicy.Resolve("POST", "/api/qcli/data-explorer/execute").Permission);
        Assert.Equal("jobs:read", CliRoutePolicy.Resolve("GET", "/api/v1/qcli/jobs/x/history").Permission);
        Assert.Equal("jobs:manage", CliRoutePolicy.Resolve("POST", "/api/v1/qcli/jobs/x/trigger").Permission);
        Assert.Equal("logs:read", CliRoutePolicy.Resolve("GET", "/ws/v1/qcli/logs").Permission);
        Assert.Equal("shell:execute", CliRoutePolicy.Resolve("GET", "/ws/qcli").Permission);
        Assert.Equal("shell:execute", CliRoutePolicy.Resolve("GET", "/ws/v1/qcli/shell").Permission);
        Assert.Equal("admin:access", CliRoutePolicy.Resolve("GET", "/api/v1/qcli/status").Permission);
        Assert.True(CliRoutePolicy.Resolve("POST", "/api/v1/qcli/sessions").IsPublic);
        Assert.False(CliRoutePolicy.Resolve("POST", "/api/v1/qcli/sessions/ws-ticket").IsPublic);
        Assert.True(CliRoutePolicy.Resolve("GET", "/api/cli/versions").IsPublic);
        Assert.False(CliRoutePolicy.Resolve("GET", "/host/anything").IsCliRoute);
    }

    // =========================================================================
    // B2 — WebSockets
    // =========================================================================

    [Fact]
    public async Task WsTicket_Shape_AndRequiresAuth()
    {
        var res = await _h.PostAsync("sessions/ws-ticket", _root);
        Assert.Equal(201, (int)res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Matches("^[0-9a-f]{64}$", body.GetProperty("ticket").GetString());
        Assert.Equal(30, body.GetProperty("expiresIn").GetInt32());

        await AssertErrorAsync(await _h.PostAsync("sessions/ws-ticket", null), 401);
    }

    [Fact]
    public async Task EventsSocket_RequiresSingleUseTicket()
    {
        Assert.Equal(401, await _h.RejectedUpgradeStatusAsync("/ws/v1/qcli/events"));
        Assert.Equal(401, await _h.RejectedUpgradeStatusAsync("/ws/v1/qcli/events?ticket=nope"));
        // Bearer tokens are never accepted in query strings.
        Assert.Equal(401, await _h.RejectedUpgradeStatusAsync($"/ws/v1/qcli/events?ticket={_root}"));
        Assert.Equal(401, await _h.RejectedUpgradeStatusAsync($"/ws/v1/qcli/events?token={_root}&_header_Authorization=Bearer%20{_root}"));

        var ticket = await _h.TicketAsync(_root);
        var socket = await _h.ConnectWebSocketAsync($"/ws/v1/qcli/events?ticket={ticket}");
        Assert.Contains("connected", await ReceiveTextAsync(socket));
        await CloseAsync(socket);

        Assert.Equal(401, await _h.RejectedUpgradeStatusAsync($"/ws/v1/qcli/events?ticket={ticket}"));
    }

    [Fact]
    public async Task Ticket_ExpiresAfter30Seconds_AndDiesWithSession()
    {
        var ticket = await _h.TicketAsync(_root);
        _h.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(401, await _h.RejectedUpgradeStatusAsync($"/ws/qcli/events?ticket={ticket}"));

        var (token, _) = await _h.LoginAsync("root", "root");
        var t2 = await _h.TicketAsync(token);
        await _h.DeleteAsync("sessions/current", token);
        Assert.Equal(401, await _h.RejectedUpgradeStatusAsync($"/ws/qcli/events?ticket={t2}"));
    }

    [Fact]
    public async Task WsUpgrade_ChecksOrigin()
    {
        Assert.Equal(403, await _h.RejectedUpgradeStatusAsync($"/ws/v1/qcli/events?ticket={await _h.TicketAsync(_root)}", "http://evil.test"));

        var ok = await _h.ConnectWebSocketAsync($"/ws/v1/qcli/events?ticket={await _h.TicketAsync(_root)}", AppOrigin);
        await CloseAsync(ok);
        var wildcardPort = await _h.ConnectWebSocketAsync($"/ws/v1/qcli/events?ticket={await _h.TicketAsync(_root)}", "http://127.0.0.1:9999");
        await CloseAsync(wildcardPort);
    }

    [Fact]
    public async Task Shell_IsDisabledByDefault_AndRequiresShellExecute()
    {
        // C5: shell rejections are accept-then-close with a readable code.
        Assert.Equal(4404, (await ShellTestClient.CloseOfAsync(_h.Host, $"/ws/v1/qcli/shell?ticket={await _h.TicketAsync(_root)}")).Code);
        Assert.Equal(4404, (await ShellTestClient.CloseOfAsync(_h.Host, $"/ws/qcli?ticket={await _h.TicketAsync(_root)}")).Code);

        await using var host = await UsersTestHost.StartAsync(configureCli: cli => cli.EnableShell());
        var root = await host.RootTokenAsync();
        await host.CreateUserAsync(root, "shell-less");
        var user = (await host.LoginAsync("shell-less", "pass1234")).Token;
        Assert.Equal(4403, (await ShellTestClient.CloseOfAsync(host.Host, $"/ws/v1/qcli/shell?ticket={await host.TicketAsync(user)}")).Code);
        Assert.Equal(4401, (await ShellTestClient.CloseOfAsync(host.Host, "/ws/v1/qcli/shell")).Code);
    }

    [Fact]
    public async Task Shell_OneShotCommand_StreamsOutputAndExit()
    {
        await using var host = await UsersTestHost.StartAsync(configureCli: cli => cli.EnableShell());
        var root = await host.RootTokenAsync();
        var socket = await host.ConnectWebSocketAsync(
            $"/ws/v1/qcli/shell?cols=80&rows=24&cmd={Uri.EscapeDataString("echo one-shot-ok")}&ticket={await host.TicketAsync(root)}");

        var stdout = new StringBuilder();
        int? exitCode = null;
        for (var i = 0; i < 50 && exitCode == null; i++)
        {
            var msg = JsonSerializer.Deserialize<JsonElement>(await ReceiveTextAsync(socket, 10000));
            switch (msg.GetProperty("type").GetString())
            {
                case "stdout": stdout.Append(msg.GetProperty("data").GetString()); break;
                case "exit": exitCode = msg.GetProperty("code").GetInt32(); break;
            }
        }

        Assert.Contains("one-shot-ok", stdout.ToString());
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task LogsSocket_RequiresLogsRead()
    {
        var plain = await UserAsync("log-less");
        Assert.Equal(403, await _h.RejectedUpgradeStatusAsync($"/ws/v1/qcli/logs?ticket={await _h.TicketAsync(plain)}"));

        var socket = await _h.ConnectWebSocketAsync($"/ws/v1/qcli/logs?ticket={await _h.TicketAsync(_root)}");
        await CloseAsync(socket);
    }

    [Fact]
    public async Task SessionKicked_IsSentOnlyToThatUsersSockets()
    {
        await _h.CreateUserAsync(_root, "kick-me");
        var (victim, victimSession) = await _h.LoginAsync("kick-me", "pass1234");

        var victimSocket = await _h.ConnectWebSocketAsync($"/ws/v1/qcli/events?ticket={await _h.TicketAsync(victim)}");
        var rootSocket = await _h.ConnectWebSocketAsync($"/ws/v1/qcli/events?ticket={await _h.TicketAsync(_root)}");
        await ReceiveTextAsync(victimSocket);
        await ReceiveTextAsync(rootSocket);

        Assert.Equal(204, (int)(await _h.DeleteAsync($"sessions/{victimSession.GetProperty("id").GetString()}", _root)).StatusCode);

        var kicked = JsonSerializer.Deserialize<JsonElement>(await ReceiveTextAsync(victimSocket));
        Assert.Equal("session-kicked", kicked.GetProperty("type").GetString());
        Assert.Equal("revoked", kicked.GetProperty("data").GetProperty("reason").GetString());

        // The root socket gets the next broadcast, not the kick.
        await _h.Service<ICliEventSocketManager>().BroadcastMessageAsync("{\"type\":\"marker\"}");
        Assert.Contains("marker", await ReceiveTextAsync(rootSocket));

        await CloseAsync(victimSocket);
        await CloseAsync(rootSocket);
    }

    [Fact]
    public async Task Cors_AllowsOnlyConfiguredOrigins()
    {
        async Task<string?> Preflight(string origin)
        {
            var req = new HttpRequestMessage(HttpMethod.Options, $"{Base}/sessions");
            req.Headers.Add("Origin", origin);
            req.Headers.Add("Access-Control-Request-Method", "POST");
            var res = await _h.Client.SendAsync(req);
            return res.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? v.Single() : null;
        }

        Assert.Equal(AppOrigin, await Preflight(AppOrigin));
        Assert.Null(await Preflight("http://evil.test"));
    }

    // =========================================================================
    // B3 — no privilege escalation
    // =========================================================================

    [Fact]
    public async Task GroupPermissions_CannotExceedCallersOwn()
    {
        var mgr = await UserAsync("group-mgr", "groups:*", "users:read");

        Assert.Equal("cannot grant permissions you do not hold: *",
            await AssertErrorAsync(await _h.PostAsync("groups", mgr, new { name = "gods", permissions = new[] { "*" } }), 403));
        Assert.Contains("users:delete",
            await AssertErrorAsync(await _h.PostAsync("groups", mgr, new { name = "deleters", permissions = new[] { "users:read", "users:delete" } }), 403));
        Assert.Equal(201, (int)(await _h.PostAsync("groups", mgr, new { name = "readers", permissions = new[] { "users:read", "groups:read" } })).StatusCode);
        await AssertErrorAsync(await _h.PutAsync("groups/readers", mgr, new { permissions = new[] { "users:*" } }), 403);
        Assert.Equal(200, (int)(await _h.PutAsync("groups/readers", mgr, new { description = "ok" })).StatusCode);

        // Adding anyone (even self) to a more privileged group is refused.
        await AssertErrorAsync(await _h.PostAsync("groups/admin/members/group-mgr", mgr), 403);
        await AssertErrorAsync(await _h.PostAsync("groups/users/members/group-mgr", mgr), 403); // users grants commands:execute
    }

    [Fact]
    public async Task UserGroups_CannotExceedCallersOwn()
    {
        var mgr = await UserAsync("user-mgr", "users:*", "commands:execute");

        await AssertErrorAsync(await _h.PostAsync("users", mgr, new { name = "sneaky", email = "s@x.io", groups = new[] { "admin" } }), 403);
        Assert.Equal(201, (int)(await _h.PostAsync("users", mgr, new { name = "normal", email = "n@x.io", groups = new[] { "users" } })).StatusCode);
        await AssertErrorAsync(await _h.PutAsync("users/normal", mgr, new { groups = new[] { "users", "admin" } }), 403);
        await AssertErrorAsync(await _h.PutAsync("users/user-mgr", mgr, new { groups = new[] { "user-mgr-group", "admin" } }), 403);
    }

    [Fact]
    public async Task ActingOnMorePrivilegedUser_IsRefused()
    {
        var mgr = await UserAsync("weak-admin", "users:*", "sessions:*", "groups:update", "commands:execute");
        var rootSession = (await ReadJsonAsync(await _h.GetAsync("sessions/current", _root))).GetProperty("id").GetString();
        const string msg = "insufficient privileges for this user";

        Assert.Equal(msg, await AssertErrorAsync(await _h.PutAsync("users/root/password", mgr, new { password = "taken-over" }), 403));
        Assert.Equal(msg, await AssertErrorAsync(await _h.DeleteAsync("users/root", mgr), 403));
        Assert.Equal(msg, await AssertErrorAsync(await _h.PutAsync("users/root", mgr, new { disabled = true }), 403));
        Assert.Equal(msg, await AssertErrorAsync(await _h.PutAsync("users/root", mgr, new { name = "toor" }), 403));
        Assert.Equal(msg, await AssertErrorAsync(await _h.DeleteAsync($"sessions/{rootSession}", mgr), 403));
        Assert.Equal(msg, await AssertErrorAsync(await _h.DeleteAsync("groups/admin/members/root", mgr), 403));

        // Peers with covered permissions can be managed.
        await _h.CreateUserAsync(_root, "peer");
        Assert.Equal(204, (int)(await _h.PutAsync("users/peer/password", mgr, new { password = "peer-new-pass" })).StatusCode);
        Assert.Equal(200, (int)(await _h.PutAsync("users/peer", mgr, new { disabled = true })).StatusCode);
        Assert.Equal(204, (int)(await _h.DeleteAsync("users/peer", mgr)).StatusCode);
        Assert.Equal(200, (int)(await _h.GetAsync("sessions/current", _root)).StatusCode);
    }

    // =========================================================================
    // B4 — default credentials and password policy
    // =========================================================================

    [Fact]
    public async Task RootWithoutConfiguredPassword_GetsRandomPassword_AndMustChangeIt()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await UsersTestHost.StartAsync(o => o.DefaultAdminPassword = null,
            configureServices: s => s.AddSingleton<ILoggerProvider>(logs));

        await AssertErrorAsync(await host.PostAsync("sessions", null, new { username = "root", password = "root" }), 401);
        var line = Assert.Single(logs.Messages, m => m.Contains("generated password"));
        var password = Regex.Match(line, "generated password: (\\S+)").Groups[1].Value;
        Assert.Equal(16, password.Length);

        var (token, session) = await host.LoginAsync("root", password);
        Assert.True(session.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());

        const string required = "password change required";
        Assert.Equal(required, await AssertErrorAsync(await host.GetAsync("users", token), 403));
        Assert.Equal(required, await AssertErrorAsync(await host.GetAsync("commands", token), 403));
        Assert.Equal(required, await AssertErrorAsync(await host.PostAsync("sessions/ws-ticket", token), 403));
        Assert.Equal(required, await AssertErrorAsync(await host.GetAsync("groups", token), 403));
        Assert.Equal(200, (int)(await host.GetAsync("sessions/current", token)).StatusCode);

        Assert.Equal(204, (int)(await host.PutAsync("users/root/password", token, new { password = "a-strong-pass" })).StatusCode);
        Assert.Equal(200, (int)(await host.GetAsync("users", token)).StatusCode);
        var me = await ReadJsonAsync(await host.GetAsync("users/root", token));
        Assert.False(me.GetProperty("mustChangePassword").GetBoolean());
    }

    [Fact]
    public async Task ExplicitRootPassword_DoesNotForceChange_ButWeakOneWarns()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await UsersTestHost.StartAsync(configureServices: s => s.AddSingleton<ILoggerProvider>(logs));
        var (_, session) = await host.LoginAsync("root", "root");
        Assert.False(session.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        Assert.Contains(logs.Messages, m => m.Contains("well-known default password"));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("generated password"));
    }

    [Fact]
    public async Task AdminReset_SetsMustChangePassword_UnlessNotTemporary()
    {
        await _h.CreateUserAsync(_root, "reset-me");
        Assert.Equal(204, (int)(await _h.PutAsync("users/reset-me/password", _root, new { password = "temp-pass-1" })).StatusCode);
        var (token, session) = await _h.LoginAsync("reset-me", "temp-pass-1");
        Assert.True(session.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        await AssertErrorAsync(await _h.GetAsync("commands", token), 403);
        await AssertErrorAsync(await _h.PutAsync("users/root/password", token, new { password = "nope-nope" }), 403);
        Assert.Equal(204, (int)(await _h.PutAsync("users/reset-me/password", token,
            new { currentPassword = "temp-pass-1", password = "my-own-pass" })).StatusCode);
        Assert.Equal(200, (int)(await _h.GetAsync("commands", token)).StatusCode);

        Assert.Equal(204, (int)(await _h.PutAsync("users/reset-me/password", _root, new { password = "perm-pass-1", temporary = false })).StatusCode);
        var (_, s2) = await _h.LoginAsync("reset-me", "perm-pass-1");
        Assert.False(s2.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
    }

    [Fact]
    public async Task PasswordPolicy_Min8Max1024()
    {
        await AssertErrorAsync(await _h.PostAsync("users", _root, new { name = "short", email = "s@s.io", password = "1234567" }), 400);
        await AssertErrorAsync(await _h.PostAsync("users", _root, new { name = "long", email = "l@s.io", password = new string('x', 1025) }), 400);
        Assert.Equal(201, (int)(await _h.PostAsync("users", _root, new { name = "ok", email = "o@s.io", password = new string('x', 1024) })).StatusCode);
    }

    // =========================================================================
    // B5 — brute force, enumeration, DoS
    // =========================================================================

    [Fact]
    public async Task ConcurrentFailedLogins_CannotExceedLockoutLimit()
    {
        await using var host = await UsersTestHost.StartAsync(o => o.MaxAttemptsPerIpPerMinute = 1000);
        var root = await host.RootTokenAsync();
        await host.CreateUserAsync(root, "racer");

        var attempts = Enumerable.Range(0, 20)
            .Select(_ => host.PostAsync("sessions", null, new { username = "racer", password = "wrong-pass" }));
        var statuses = (await Task.WhenAll(attempts)).Select(r => (int)r.StatusCode).ToList();

        Assert.Equal(5, statuses.Count(s => s == 401));
        Assert.Equal(15, statuses.Count(s => s == 429));
        await AssertErrorAsync(await host.PostAsync("sessions", null, new { username = "racer", password = "pass1234" }), 429);
    }

    [Fact]
    public async Task UnknownUsers_AreLockedLikeKnownOnes()
    {
        for (var i = 0; i < 5; i++)
            await AssertErrorAsync(await _h.PostAsync("sessions", null, new { username = "ghost", password = "whatever" }), 401);
        Assert.Equal("account locked, try again later",
            await AssertErrorAsync(await _h.PostAsync("sessions", null, new { username = "GHOST", password = "whatever" }), 429));
    }

    [Fact]
    public async Task PerIpLimiter_Allows20PerMinute()
    {
        await using var host = await UsersTestHost.StartAsync();
        for (var i = 0; i < 20; i++)
            await host.PostAsync("sessions", null, new { username = $"nobody{i}", password = "whatever" });
        Assert.Equal("too many requests, try again later",
            await AssertErrorAsync(await host.PostAsync("sessions", null, new { username = "root", password = "root" }), 429));

        // X-Forwarded-For is ignored without TrustProxy.
        var spoofed = new HttpRequestMessage(HttpMethod.Post, $"{Base}/sessions")
        {
            Content = new StringContent("{\"username\":\"root\",\"password\":\"root\"}", Encoding.UTF8, "application/json"),
        };
        spoofed.Headers.Add("X-Forwarded-For", "10.0.0.99");
        await AssertErrorAsync(await host.Client.SendAsync(spoofed), 429);

        host.Time.Advance(TimeSpan.FromSeconds(61));
        await host.LoginAsync("root", "root");
    }

    [Fact]
    public async Task PerIpLimiter_UsesForwardedForOnlyWithTrustProxy()
    {
        await using var host = await UsersTestHost.StartAsync(o => o.TrustProxy = true);

        var n = 0;
        async Task<int> Attempt(string ip)
        {
            // Distinct names so per-user lockout does not interfere.
            var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/sessions")
            {
                Content = new StringContent($"{{\"username\":\"nobody{n++}\",\"password\":\"x\"}}", Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-Forwarded-For", ip + ", 10.9.9.9");
            return (int)(await host.Client.SendAsync(req)).StatusCode;
        }

        for (var i = 0; i < 20; i++) await Attempt("10.0.0.1");
        Assert.Equal(429, await Attempt("10.0.0.1"));
        Assert.NotEqual(429, await Attempt("10.0.0.2"));
    }

    [Fact]
    public async Task SelfPasswordChange_GoesThroughLockoutAndAudit()
    {
        await _h.CreateUserAsync(_root, "self-locker");
        var (token, _) = await _h.LoginAsync("self-locker", "pass1234");
        for (var i = 0; i < 5; i++)
            await AssertErrorAsync(await _h.PutAsync("users/self-locker/password", token, new { currentPassword = "wrong-one", password = "new-pass-1" }), 403);
        await AssertErrorAsync(await _h.PutAsync("users/self-locker/password", token, new { currentPassword = "pass1234", password = "new-pass-1" }), 429);

        var audit = await ReadJsonAsync(await _h.GetAsync("audit?event=login_failed&username=self-locker", _root));
        Assert.Equal(5, audit.GetArrayLength());

        // Admin reset clears the lockout.
        Assert.Equal(204, (int)(await _h.PutAsync("users/self-locker/password", _root, new { password = "reset-pass-1", temporary = false })).StatusCode);
        await _h.LoginAsync("self-locker", "reset-pass-1");
    }

    [Fact]
    public async Task AbsoluteSessionLifetime_IsEnforced()
    {
        await using var host = await UsersTestHost.StartAsync(o => o.MaxSessionLifetimeMs = 60 * 60 * 1000);
        var (token, session) = await host.LoginAsync("root", "root");
        Assert.Equal(session.GetProperty("lastActivity").GetInt64() + 30 * 60 * 1000, session.GetProperty("expiresAt").GetInt64());

        host.Time.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(200, (int)(await host.GetAsync("sessions/current", token)).StatusCode);
        host.Time.Advance(TimeSpan.FromMinutes(20));
        var current = await ReadJsonAsync(await host.GetAsync("sessions/current", token));
        // Idle expiry would be login+70 min; the absolute lifetime (login+60) wins.
        Assert.Equal(current.GetProperty("loginTime").GetInt64() + 60 * 60 * 1000, current.GetProperty("expiresAt").GetInt64());

        host.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(200, (int)(await host.GetAsync("sessions/current", token)).StatusCode);
        host.Time.Advance(TimeSpan.FromMinutes(6));
        await AssertErrorAsync(await host.GetAsync("sessions/current", token), 401);
    }

    // =========================================================================
    // B6 — tokens and secrets
    // =========================================================================

    [Fact]
    public async Task SessionStore_KeepsOnlyTokenHashes()
    {
        var store = _h.Service<ISessionStore>();
        Assert.Null(await store.GetSessionAsync(_root));

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_root))).ToLowerInvariant();
        var session = await store.GetSessionAsync(hash);
        Assert.NotNull(session);
        Assert.All(await store.GetActiveSessionsAsync(), s => Assert.NotEqual(_root, s.TokenHash));
    }

    [Fact]
    public async Task TokensAndPasswords_NeverAppearInAuditOrErrors()
    {
        var ticket = await _h.TicketAsync(_root);
        await _h.PostAsync("sessions", null, new { username = "root", password = "wrong-secret-xyz" });
        var bad = await _h.GetAsync("users", "invalid-token-abc");
        Assert.DoesNotContain("invalid-token-abc", await bad.Content.ReadAsStringAsync());

        var audit = await (await _h.GetAsync("audit", _root)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(_root, audit);
        Assert.DoesNotContain(ticket, audit);
        Assert.DoesNotContain("wrong-secret-xyz", audit);
        Assert.DoesNotContain(AuthServiceHash(_root), audit);
    }

    private static string AuthServiceHash(string token) => AuthService.HashToken(token);

    // =========================================================================
    // B7 — input validation
    // =========================================================================

    [Theory]
    [InlineData("bad name")]
    [InlineData("-leading")]
    [InlineData("tab\there")]
    [InlineData("esc\u001b[31m")]
    [InlineData("x/y")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task InvalidNames_Return400(string name)
    {
        await AssertErrorAsync(await _h.PostAsync("users", _root, new { name, email = "v@x.io" }), 400);
        await AssertErrorAsync(await _h.PostAsync("groups", _root, new { name }), 400);
    }

    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("two@@x.io")]
    [InlineData("sp ace@x.io")]
    [InlineData("ctl\u0007@x.io")]
    public async Task InvalidEmails_Return400(string email)
    {
        await AssertErrorAsync(await _h.PostAsync("users", _root, new { name = "valid-name", email }), 400);
    }

    [Fact]
    public async Task EmailWithoutTld_IsValid_AndLengthIsCapped()
    {
        Assert.Equal(201, (int)(await _h.PostAsync("users", _root, new { name = "local-user", email = "local@localhost" })).StatusCode);
        var tooLong = new string('a', 250) + "@x.io";
        await AssertErrorAsync(await _h.PostAsync("users", _root, new { name = "long-mail", email = tooLong }), 400);
    }

    [Fact]
    public async Task GroupFieldLimits()
    {
        await AssertErrorAsync(await _h.PostAsync("groups", _root, new { name = "g1", permissions = new[] { "Users Read" } }), 400);
        await AssertErrorAsync(await _h.PostAsync("groups", _root, new { name = "g2", permissions = new[] { "users::read" } }), 400);
        await AssertErrorAsync(await _h.PostAsync("groups", _root, new { name = "g3", permissions = new[] { new string('a', 129) } }), 400);
        await AssertErrorAsync(await _h.PostAsync("groups", _root, new { name = "g4", permissions = Enumerable.Range(0, 101).Select(i => $"p{i}:x").ToArray() }), 400);
        await AssertErrorAsync(await _h.PostAsync("groups", _root, new { name = "g5", description = new string('d', 257) }), 400);
        await AssertErrorAsync(await _h.PostAsync("groups", _root, new { name = "g6", description = "bell\u0007" }), 400);
        Assert.Equal(201, (int)(await _h.PostAsync("groups", _root, new
        {
            name = "g7",
            description = new string('d', 256),
            permissions = new[] { "Users:READ", "files:*", "*", "a_b.c-d:e" },
        })).StatusCode);
    }

    [Fact]
    public async Task DataAndBodySizeLimits()
    {
        var big = new string('x', 17 * 1024);
        await AssertErrorAsync(await _h.PostAsync("users", _root, new { name = "data-user", email = "d@x.io", data = new { big } }), 400);
        await _h.CreateUserAsync(_root, "data-user2");
        await AssertErrorAsync(await _h.PutAsync("users/data-user2", _root, new { data = new { big } }), 400);

        var huge = new string('y', 1024 * 1024 + 10);
        await AssertErrorAsync(await _h.PostAsync("users", _root, new { name = "huge", email = "h@x.io", homeDir = huge }), 400);
    }

    [Fact]
    public async Task NamesAndEmails_ShareOneNamespace()
    {
        await _h.CreateUserAsync(_root, "carol", email: "carol@corp.io");
        await AssertErrorAsync(await _h.PostAsync("users", _root, new { name = "carol@corp.io", email = "other@corp.io" }), 409);
        await _h.CreateUserAsync(_root, "dave@corp.io", email: "dave-mail@corp.io");
        await AssertErrorAsync(await _h.PostAsync("users", _root, new { name = "dave2", email = "DAVE@corp.io" }), 409);
        await AssertErrorAsync(await _h.PutAsync("users/carol", _root, new { email = "dave@corp.io" }), 409);
    }

    // =========================================================================
    // B8 — audit
    // =========================================================================

    [Fact]
    public async Task Audit_HasUserAndActorIds_AndSanitizesUnknownNames()
    {
        var created = await _h.CreateUserAsync(_root, "audited");
        var rootId = (await ReadJsonAsync(await _h.GetAsync("users/root", _root))).GetProperty("id").GetString();

        var entry = (await ReadJsonAsync(await _h.GetAsync("audit?event=user_created", _root))).EnumerateArray().Single();
        Assert.Equal(created.GetProperty("id").GetString(), entry.GetProperty("userId").GetString());
        Assert.Equal(rootId, entry.GetProperty("actorId").GetString());

        var evil = "evil\u001b[31m" + new string('z', 100);
        await _h.PostAsync("sessions", null, new { username = evil, password = "whatever" });
        var failed = (await ReadJsonAsync(await _h.GetAsync("audit?event=login_failed", _root))).EnumerateArray().First();
        var name = failed.GetProperty("username").GetString()!;
        Assert.Equal(64, name.Length);
        Assert.DoesNotContain('\u001b', name);
        Assert.StartsWith("evil[31m", name);
    }

    [Fact]
    public async Task FailedLogins_CannotEvictOtherHistory()
    {
        var store = new InMemoryAuditStore();
        for (var i = 0; i < 10; i++)
            await store.LogAsync(new Qodalis.Cli.Plugin.Users.Models.AuditEntry { Timestamp = i, Event = "user_created", Username = $"u{i}" });
        for (var i = 0; i < 500; i++)
            await store.LogAsync(new Qodalis.Cli.Plugin.Users.Models.AuditEntry { Timestamp = 100 + i, Event = "login_failed", Username = "x" });

        var all = await store.GetEntriesAsync();
        Assert.Equal(10, all.Count(e => e.Event == "user_created"));
        Assert.Equal(200, all.Count(e => e.Event == "login_failed"));
    }

    // =========================================================================
    // B9 — CSRF
    // =========================================================================

    [Fact]
    public async Task StateChange_WithDisallowedOrigin_AndNoBearer_Returns403()
    {
        async Task<HttpResponseMessage> Login(string? origin)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/sessions")
            {
                Content = new StringContent("{\"username\":\"root\",\"password\":\"root\"}", Encoding.UTF8, "application/json"),
            };
            if (origin != null) req.Headers.Add("Origin", origin);
            return await _h.Client.SendAsync(req);
        }

        await AssertErrorAsync(await Login("http://evil.test"), 403);
        Assert.Equal(201, (int)(await Login(AppOrigin)).StatusCode);
        Assert.Equal(201, (int)(await Login(null)).StatusCode);

        // Bearer-authenticated requests are not cookie-driven; Origin is irrelevant for them.
        var withBearer = new HttpRequestMessage(HttpMethod.Post, $"{Base}/groups")
        {
            Content = new StringContent("{\"name\":\"bearer-group\"}", Encoding.UTF8, "application/json"),
        };
        withBearer.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _root);
        withBearer.Headers.Add("Origin", "http://evil.test");
        Assert.Equal(201, (int)(await _h.Client.SendAsync(withBearer)).StatusCode);
    }

    [Fact]
    public async Task StateChange_WithSameOrigin_NotInAllowedOrigins_Succeeds()
    {
        // The admin dashboard is served by the server itself, so the browser sends the server's own
        // origin (TestServer: http://localhost). Same-origin requests are never cross-site.
        async Task<HttpResponseMessage> Login(string origin)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/sessions")
            {
                Content = new StringContent("{\"username\":\"root\",\"password\":\"root\"}", Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("Origin", origin);
            return await _h.Client.SendAsync(req);
        }

        Assert.Equal(201, (int)(await Login("http://localhost")).StatusCode);
        Assert.Equal(201, (int)(await Login("HTTP://LocalHost:80")).StatusCode);

        // A different scheme or port is a different origin and still rejected.
        await AssertErrorAsync(await Login("http://localhost:8046"), 403);
        await AssertErrorAsync(await Login("https://localhost"), 403);
        await AssertErrorAsync(await Login("http://evil.test"), 403);
    }

    [Fact]
    public void IsSameOrigin_HonoursRequestHostAndNormalizesDefaultPorts()
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new Microsoft.AspNetCore.Http.HostString("localhost:8046");
        var options = new CliServerOptions();
        options.AllowedOrigins.Add(AppOrigin);

        Assert.True(options.IsOriginAllowed("http://localhost:8046", ctx.Request));
        Assert.True(options.IsOriginAllowed("http://LOCALHOST:8046/", ctx.Request));
        Assert.True(options.IsOriginAllowed(AppOrigin, ctx.Request));
        Assert.False(options.IsOriginAllowed("http://localhost:8047", ctx.Request));
        Assert.False(options.IsOriginAllowed("null", ctx.Request));
        Assert.False(options.IsOriginAllowed(null, ctx.Request));

        ctx.Request.Scheme = "https";
        ctx.Request.Host = new Microsoft.AspNetCore.Http.HostString("example.com");
        Assert.True(CliServerOptions.IsSameOrigin("https://example.com:443", ctx.Request));
        Assert.False(CliServerOptions.IsSameOrigin("http://example.com", ctx.Request));
    }

    // =========================================================================
    // B10 — admin dashboard
    // =========================================================================

    [Fact]
    public async Task AdminMiddleware_MatchesLoginPathExactly()
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(s => s.AddControllers().AddCli(cli => cli.AddAdmin(o =>
            {
                o.Username = "admin";
                o.Password = "admin-pass";
                o.JwtSecret = "security-test-secret-that-is-long-enough-for-hmac";
            })));
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseQodalisAdmin();
                app.UseEndpoints(e => e.MapControllers());
                app.UseCli();
            });
        });
        using var host = await builder.StartAsync();
        var client = host.GetTestClient();

        Assert.Equal(401, (int)(await client.GetAsync("/api/v1/qcli/status/auth/login")).StatusCode);
        Assert.Equal(401, (int)(await client.GetAsync("/api/v1/qcli/status")).StatusCode);
        var login = await client.PostAsync("/api/v1/qcli/auth/login",
            new StringContent("{\"username\":\"admin\",\"password\":\"admin-pass\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(200, (int)login.StatusCode);
        await host.StopAsync();
    }
}
