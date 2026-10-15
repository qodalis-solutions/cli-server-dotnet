using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;
using static Qodalis.Cli.Tests.Helpers.ShellTestClient;

namespace Qodalis.Cli.Tests;

/// <summary>
/// Remote shell security (web-cli spec "Appendix C — Remote shell security", C1–C5) without the
/// users plugin: access modes, locked default, limits, timeouts, environment, logging.
/// </summary>
public class ShellSecurityTests
{
    private const string Shell = "/bin/sh";
    private const string Path = "/ws/v1/qcli/shell";
    private const string Secret = "s3cret-shell-token";

    private static Action<CliShellOptions> Base(Action<CliShellOptions>? more = null) => o =>
    {
        o.Shell = Shell;
        more?.Invoke(o);
    };

    private static string Cmd(string command) => $"{Path}?cols=80&rows=24&cmd={Uri.EscapeDataString(command)}";

    private static Action<HttpRequest> Bearer(string token) => r => r.Headers.Authorization = $"Bearer {token}";

    // ------------------------------------------------------------------ C1 modes

    [Fact]
    public async Task Disabled_ClosesWith4404_AndCapabilitiesReportShellFalse()
    {
        await using var h = await ShellTestHost.StartAsync();
        var close = await CloseOfAsync(h.Host, Path);
        Assert.Equal(4404, close.Code);
        Assert.Equal("shell disabled", close.Reason);
        Assert.Equal(4404, (await CloseOfAsync(h.Host, "/ws/qcli")).Code);

        var caps = await h.CapabilitiesAsync();
        Assert.False(caps.GetProperty("shell").GetBoolean());
        Assert.False(caps.TryGetProperty("shellAuth", out _));
    }

    [Fact]
    public async Task EnabledWithoutMode_IsLocked_ClosesWith4401_AndLogsError()
    {
        await using var h = await ShellTestHost.StartAsync(cli => cli.EnableShell());
        var close = await CloseOfAsync(h.Host, Cmd("echo hi"));
        Assert.Equal(4401, close.Code);
        Assert.Equal("shell access is not configured", close.Reason);
        Assert.False(close.GotReady);
        Assert.Contains(h.Logs.Messages, m => m.Contains("locked", StringComparison.OrdinalIgnoreCase) && m.Contains("Token"));

        var caps = await h.CapabilitiesAsync();
        Assert.True(caps.GetProperty("shell").GetBoolean());
        Assert.Equal("locked", caps.GetProperty("shellAuth").GetString());
    }

    [Fact]
    public async Task Anonymous_RequiresExplicitOptIn_AndLogsWarning()
    {
        await using var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o => o.AllowAnonymous = true)));
        var close = await CloseOfAsync(h.Host, Cmd("echo anon-ok"));
        Assert.True(close.GotReady);
        Assert.Contains("anon-ok", close.Stdout);
        Assert.Equal(0, close.ExitCode);
        Assert.Contains(h.Logs.Messages, m => m.Contains("shell is enabled without authentication"));
        Assert.Equal("anonymous", (await h.CapabilitiesAsync()).GetProperty("shellAuth").GetString());
    }

    [Fact]
    public async Task Token_ViaAuthorizationHeader()
    {
        await using var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o => o.Token = Secret)));
        Assert.Equal("token", (await h.CapabilitiesAsync()).GetProperty("shellAuth").GetString());

        var ok = await CloseOfAsync(h.Host, Cmd("echo token-ok"), Bearer(Secret));
        Assert.Contains("token-ok", ok.Stdout);

        var missing = await CloseOfAsync(h.Host, Cmd("echo no"));
        Assert.Equal(4401, missing.Code);
        Assert.Equal("login required", missing.Reason);
        Assert.False(missing.GotReady);

        var wrong = await CloseOfAsync(h.Host, Cmd("echo no"), Bearer("s3cret-shell-tokeX"));
        Assert.Equal(4403, wrong.Code);
        Assert.Equal("forbidden", wrong.Reason);
        Assert.False(wrong.GotReady);

        var basic = await CloseOfAsync(h.Host, Cmd("echo no"), r => r.Headers.Authorization = $"Basic {Secret}");
        Assert.Equal(4401, basic.Code);

        // The token never appears in logs.
        Assert.DoesNotContain(h.Logs.Messages, m => m.Contains(Secret) || m.Contains("tokeX"));
    }

    [Fact]
    public async Task Token_ViaHeaderQueryParameter()
    {
        await using var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o => o.Token = Secret)));

        var ok = await CloseOfAsync(h.Host, Cmd("echo query-ok") + "&_header_Authorization=" + Uri.EscapeDataString($"Bearer {Secret}"));
        Assert.Contains("query-ok", ok.Stdout);

        var wrong = await CloseOfAsync(h.Host, Cmd("echo no") + "&_header_Authorization=" + Uri.EscapeDataString("Bearer nope"));
        Assert.Equal(4403, wrong.Code);
        Assert.DoesNotContain(h.Logs.Messages, m => m.Contains(Secret));
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abd", "abc", false)]
    [InlineData("ab", "abc", false)]
    [InlineData("abcd", "abc", false)]
    [InlineData("", "abc", false)]
    [InlineData(null, "abc", false)]
    [InlineData("abc", null, false)]
    [InlineData("ABC", "abc", false)]
    public void TokenMatches_IsExactAndConstantTime(string? presented, string? expected, bool match)
    {
        Assert.Equal(match, CliShellAccess.TokenMatches(presented, expected));
    }

    [Fact]
    public void TokenComparison_UsesFixedTimeEquals()
    {
        // Guard against regressions to a short-circuiting comparison.
        var source = File.ReadAllText(System.IO.Path.Combine(FindRepoRoot(), "src", "Qodalis.Cli", "Security", "CliShellAccess.cs"));
        Assert.Contains("CryptographicOperations.FixedTimeEquals", source);
    }

    [Fact]
    public async Task Authorize_AllowsDeniesAndTreatsExceptionsAsDeny()
    {
        await using var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o =>
            o.Authorize = ctx => ctx.Request.Query["key"].FirstOrDefault() switch
            {
                "good" => ValueTask.FromResult(true),
                "throw" => throw new InvalidOperationException("boom"),
                _ => ValueTask.FromResult(false),
            })));
        Assert.Equal("custom", (await h.CapabilitiesAsync()).GetProperty("shellAuth").GetString());

        Assert.Contains("custom-ok", (await CloseOfAsync(h.Host, Cmd("echo custom-ok") + "&key=good")).Stdout);
        Assert.Equal(4403, (await CloseOfAsync(h.Host, Cmd("echo no") + "&key=bad")).Code);
        var thrown = await CloseOfAsync(h.Host, Cmd("echo no") + "&key=throw");
        Assert.Equal(4403, thrown.Code);
        Assert.False(thrown.GotReady);
    }

    [Fact]
    public async Task AsyncAuthorize_IsAwaited()
    {
        await using var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o =>
            o.Authorize = async ctx =>
            {
                await Task.Delay(10);
                return ctx.Request.Headers["X-Api-Key"] == "k";
            })));
        Assert.Contains("async-ok", (await CloseOfAsync(h.Host, Cmd("echo async-ok"), r => r.Headers["X-Api-Key"] = "k")).Stdout);
        Assert.Equal(4403, (await CloseOfAsync(h.Host, Cmd("echo no"))).Code);
    }

    [Fact]
    public async Task TokenAndAuthorize_BothMustPass()
    {
        var allow = false;
        await using var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o =>
        {
            o.Token = Secret;
            o.Authorize = _ => ValueTask.FromResult(allow);
        })));
        Assert.Equal("token", (await h.CapabilitiesAsync()).GetProperty("shellAuth").GetString());

        Assert.Equal(4403, (await CloseOfAsync(h.Host, Cmd("echo no"), Bearer(Secret))).Code);
        allow = true;
        Assert.Contains("both-ok", (await CloseOfAsync(h.Host, Cmd("echo both-ok"), Bearer(Secret))).Stdout);
        Assert.Equal(4401, (await CloseOfAsync(h.Host, Cmd("echo no"))).Code);
    }

    [Fact]
    public async Task AllowAnonymous_IsIgnoredOnceTokenOrAuthorizeIsSet()
    {
        await using (var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o =>
        {
            o.Token = Secret;
            o.AllowAnonymous = true;
        }))))
        {
            Assert.Equal("token", (await h.CapabilitiesAsync()).GetProperty("shellAuth").GetString());
            Assert.Equal(4401, (await CloseOfAsync(h.Host, Cmd("echo no"))).Code);
            Assert.DoesNotContain(h.Logs.Messages, m => m.Contains("without authentication"));
        }

        await using (var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o =>
        {
            o.Authorize = _ => ValueTask.FromResult(false);
            o.AllowAnonymous = true;
        }))))
        {
            Assert.Equal("custom", (await h.CapabilitiesAsync()).GetProperty("shellAuth").GetString());
            Assert.Equal(4403, (await CloseOfAsync(h.Host, Cmd("echo no"))).Code);
        }
    }

    [Fact]
    public async Task ServerShutdown_ClosesOpenShellsWith1001()
    {
        var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o => o.AllowAnonymous = true)));
        var socket = await ConnectAsync(h.Host, Path);
        await ExpectReadyAsync(socket);

        var reading = ReadUntilCloseAsync(socket, 10000);
        var stopping = h.DisposeAsync().AsTask();
        var close = await reading;
        await stopping;
        Assert.Equal(1001, close.Code);
        Assert.Equal("server shutdown", close.Reason);
        Assert.Contains(h.Logs.Messages, m => m.StartsWith("Shell session closed") && m.Contains("reason=server shutdown"));
    }

    [Fact]
    public async Task OriginRejection_StaysHttp403()
    {
        await using var h = await ShellTestHost.StartAsync(cli =>
        {
            cli.SetAllowedOrigins("http://localhost:4300");
            cli.EnableShell(Base(o => o.AllowAnonymous = true));
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectAsync(h.Host, Path, r => r.Headers.Origin = "http://evil.test"));
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task SameOriginUpgrade_PassesOriginCheck()
    {
        await using var h = await ShellTestHost.StartAsync(cli =>
        {
            cli.SetAllowedOrigins("http://localhost:4300");
            cli.EnableShell(Base(o => o.AllowAnonymous = true));
        });
        using var socket = await ConnectAsync(h.Host, Path, r => r.Headers.Origin = "http://localhost");
        await ExpectReadyAsync(socket);
    }

    // ------------------------------------------------------------------ C3 limits

    [Fact]
    public async Task PerAddressLimit_ClosesWith4429_AndFreesSlotOnClose()
    {
        await using var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o =>
        {
            o.AllowAnonymous = true;
            o.MaxSessionsPerUser = 2;
        })));

        var a1 = await ConnectAsync(h.Host, Path, FromAddress("10.0.0.1"));
        await ExpectReadyAsync(a1);
        var a2 = await ConnectAsync(h.Host, Path, FromAddress("10.0.0.1"));
        await ExpectReadyAsync(a2);

        var third = await CloseOfAsync(h.Host, Path, FromAddress("10.0.0.1"));
        Assert.Equal(4429, third.Code);
        Assert.Equal("too many shell sessions", third.Reason);

        // Another address has its own budget.
        var b1 = await ConnectAsync(h.Host, Path, FromAddress("10.0.0.2"));
        await ExpectReadyAsync(b1);

        // Closing a shell frees its slot.
        await a1.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await WaitUntilAsync(() => h.Host.Services.GetService<ICliShellSessions>()!.Count == 2);
        var again = await ConnectAsync(h.Host, Path, FromAddress("10.0.0.1"));
        await ExpectReadyAsync(again);

        foreach (var s in new[] { a2, b1, again }) s.Abort();
    }

    [Fact]
    public async Task GlobalLimit_ClosesWith4429()
    {
        await using var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o =>
        {
            o.AllowAnonymous = true;
            o.MaxSessionsPerUser = 0;
            o.MaxSessions = 1;
        })));

        var first = await ConnectAsync(h.Host, Path, FromAddress("10.0.0.1"));
        await ExpectReadyAsync(first);
        Assert.Equal(4429, (await CloseOfAsync(h.Host, Path, FromAddress("10.0.0.9"))).Code);
        first.Abort();
    }

    [Fact]
    public async Task IdleTimeout_ClosesWith4408_StdinKeepsAlive()
    {
        await using var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o =>
        {
            o.AllowAnonymous = true;
            o.IdleTimeout = TimeSpan.FromMilliseconds(600);
        })));

        var socket = await ConnectAsync(h.Host, Path);
        await ExpectReadyAsync(socket);
        for (var i = 0; i < 8; i++)
        {
            await SendStdinAsync(socket, ":\n");
            await Task.Delay(150);
        }
        Assert.Equal(WebSocketState.Open, socket.State);

        var close = await ReadUntilCloseAsync(socket, 10000);
        Assert.Equal(4408, close.Code);
        Assert.Equal("shell timed out", close.Reason);
        await WaitUntilAsync(() => h.Host.Services.GetService<ICliShellSessions>()!.Count == 0);
        await WaitUntilAsync(() => h.Logs.Messages.Any(m => m.StartsWith("Shell session closed") && m.Contains("reason=shell timed out")));
    }

    [Fact]
    public async Task LargeFragmentedAndMalformedInput_DoesNotEndTheShell()
    {
        await using var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o =>
        {
            o.AllowAnonymous = true;
            o.IdleTimeout = null;
        })));

        var socket = await ConnectAsync(h.Host, Path);
        await ExpectReadyAsync(socket);

        // A malformed message is ignored.
        await socket.SendAsync("not json"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        await socket.SendAsync("{\"data\":\"x\"}"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);

        // A stdin message larger than the server's receive buffer (a paste), sent in two frames.
        var paste = string.Concat(Enumerable.Repeat(": 0123456789abcdef\n", 600)) + "echo big-$((40+2))\nexit\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "stdin", data = paste }));
        Assert.True(bytes.Length > 8192);
        var half = bytes.Length / 2;
        await socket.SendAsync(bytes.AsMemory(0, half), WebSocketMessageType.Text, false, CancellationToken.None);
        await socket.SendAsync(bytes.AsMemory(half), WebSocketMessageType.Text, true, CancellationToken.None);

        var close = await ReadUntilCloseAsync(socket, 15000);
        Assert.Contains("big-42", close.Stdout);
        Assert.Equal(0, close.ExitCode);
    }

    [Fact]
    public async Task MaxDuration_ClosesWith4408()
    {
        await using var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o =>
        {
            o.AllowAnonymous = true;
            o.IdleTimeout = null;
            o.MaxDuration = TimeSpan.FromMilliseconds(700);
        })));

        var started = DateTime.UtcNow;
        var close = await CloseOfAsync(h.Host, Path);
        Assert.True(close.GotReady);
        Assert.Equal(4408, close.Code);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(600));
    }

    [Fact]
    public async Task Environment_IsMinimal_WithAllowAndSet()
    {
        Environment.SetEnvironmentVariable("QCLI_TEST_SERVER_SECRET", "db-password-leak");
        Environment.SetEnvironmentVariable("QCLI_TEST_ALLOWED", "allowed-value");
        try
        {
            await using var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o =>
            {
                o.AllowAnonymous = true;
                o.EnvironmentAllow.Add("QCLI_TEST_ALLOWED");
                o.EnvironmentSet["QCLI_TEST_SET"] = "set-value";
            })));

            var close = await CloseOfAsync(h.Host, Cmd("env"));
            Assert.Equal(0, close.ExitCode);
            var env = close.Stdout;
            Assert.DoesNotContain("QCLI_TEST_SERVER_SECRET", env);
            Assert.DoesNotContain("db-password-leak", env);
            Assert.Contains("QCLI_TEST_ALLOWED=allowed-value", env);
            Assert.Contains("QCLI_TEST_SET=set-value", env);
            Assert.Contains("TERM=xterm-256color", env);
            Assert.Contains("COLUMNS=80", env);
            Assert.Contains($"SHELL={Shell}", env);
            Assert.Contains("PATH=", env);
        }
        finally
        {
            Environment.SetEnvironmentVariable("QCLI_TEST_SERVER_SECRET", null);
            Environment.SetEnvironmentVariable("QCLI_TEST_ALLOWED", null);
        }
    }

    [Fact]
    public async Task WorkingDirectory_IsConfigurable()
    {
        var dir = Directory.CreateTempSubdirectory("qcli-shell-cwd-");
        try
        {
            await using var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o =>
            {
                o.AllowAnonymous = true;
                o.WorkingDirectory = dir.FullName;
            })));
            Assert.Contains(dir.Name, (await CloseOfAsync(h.Host, Cmd("pwd"))).Stdout);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public async Task Logs_OpenAndClose_WithAddressAndMode_ButNeverTheCommand()
    {
        await using var h = await ShellTestHost.StartAsync(cli => cli.EnableShell(Base(o => o.AllowAnonymous = true)));
        await CloseOfAsync(h.Host, Cmd("echo hidden-command-text-42"), FromAddress("10.1.2.3"));
        await WaitUntilAsync(() => h.Logs.Messages.Any(m => m.StartsWith("Shell session closed")));

        Assert.Contains(h.Logs.Messages, m => m.StartsWith("Shell session opened") && m.Contains("10.1.2.3") && m.Contains("mode=command") && m.Contains("auth=anonymous"));
        Assert.Contains(h.Logs.Messages, m => m.StartsWith("Shell session closed") && m.Contains("10.1.2.3") && m.Contains("exitCode=0"));
        Assert.DoesNotContain(h.Logs.Messages, m => m.Contains("echo hidden-command-text-42"));
    }

    // ------------------------------------------------------------------ helpers

    internal static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("condition not met in time");
            await Task.Delay(25);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "src", "Qodalis.Cli.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}

/// <summary>Remote shell security with the users plugin (C1 users mode, C2 session binding, C4 audit).</summary>
public class ShellUsersModeTests
{
    private const string Path = "/ws/v1/qcli/shell";

    private static Task<UsersTestHost> StartAsync(Action<CliShellOptions>? configure = null,
        Action<Qodalis.Cli.Plugin.Users.UsersModuleOptions>? users = null) =>
        UsersTestHost.StartAsync(configure: users, configureCli: cli => cli.EnableShell(o =>
        {
            o.Shell = "/bin/sh";
            o.SessionCheckInterval = TimeSpan.FromMilliseconds(200);
            configure?.Invoke(o);
        }));

    private static async Task<WebSocket> OpenAsync(UsersTestHost h, string token, string extra = "")
    {
        var socket = await ConnectAsync(h.Host, $"{Path}?ticket={await h.TicketAsync(token)}{extra}");
        await ExpectReadyAsync(socket);
        return socket;
    }

    [Fact]
    public async Task UsersMode_IsReported_AndRequiresTicket_EvenWithoutProtectServer()
    {
        await using var h = await StartAsync(users: o => o.ProtectServer = false);
        var caps = UsersTestHost.ReadJsonAsync(await h.GetAsync("capabilities"));
        Assert.Equal("users", (await caps).GetProperty("shellAuth").GetString());

        Assert.Equal(4401, (await CloseOfAsync(h.Host, Path)).Code);
        Assert.Equal(4401, (await CloseOfAsync(h.Host, $"{Path}?ticket=bogus")).Code);

        var root = await h.RootTokenAsync();
        var socket = await OpenAsync(h, root);
        socket.Abort();
    }

    [Fact]
    public async Task UsersMode_WithToken_BothMustPass()
    {
        await using var h = await StartAsync(o => o.Token = "extra-secret");
        var root = await h.RootTokenAsync();

        Assert.Equal(4401, (await CloseOfAsync(h.Host, $"{Path}?ticket={await h.TicketAsync(root)}")).Code);
        var socket = await OpenAsync(h, root, "&_header_Authorization=" + Uri.EscapeDataString("Bearer extra-secret"));
        socket.Abort();
    }

    [Fact]
    public async Task Logout_ClosesTheShellWith4401()
    {
        await using var h = await StartAsync();
        var root = await h.RootTokenAsync();
        var socket = await OpenAsync(h, root);

        Assert.Equal(204, (int)(await h.DeleteAsync("sessions/current", root)).StatusCode);
        var close = await ReadUntilCloseAsync(socket, 10000);
        Assert.Equal(4401, close.Code);
        Assert.Equal("session ended", close.Reason);
        await ShellSecurityTests.WaitUntilAsync(() => h.Service<ICliShellSessions>().Count == 0);
    }

    [Fact]
    public async Task AdminKick_ClosesTheShellWith4401()
    {
        await using var h = await StartAsync();
        var root = await h.RootTokenAsync();
        var userToken = await h.UserWithPermissionsAsync(root, "sheller", "shell:execute");
        var socket = await OpenAsync(h, userToken);

        var sessionId = h.Service<ICliShellSessions>().GetSessions().Single().AuthSessionId;
        Assert.NotNull(sessionId);
        Assert.Equal(204, (int)(await h.DeleteAsync($"sessions/{sessionId}", root)).StatusCode);

        Assert.Equal(4401, (await ReadUntilCloseAsync(socket, 10000)).Code);
    }

    [Fact]
    public async Task LosingShellExecute_ClosesTheShellWith4401()
    {
        await using var h = await StartAsync(o => o.SessionCheckInterval = TimeSpan.FromSeconds(60));
        var root = await h.RootTokenAsync();
        var userToken = await h.UserWithPermissionsAsync(root, "temp-sheller", "shell:execute");
        var socket = await OpenAsync(h, userToken);

        // The group change triggers an immediate re-check (no 60 s wait).
        var res = await h.PutAsync("groups/temp-sheller-group", root, new { permissions = new[] { "commands:execute" } });
        Assert.Equal(200, (int)res.StatusCode);

        var close = await ReadUntilCloseAsync(socket, 10000);
        Assert.Equal(4401, close.Code);
        Assert.Equal("session ended", close.Reason);

        // Audit distinguishes the cause.
        var closed = await LatestShellClosedAsync(h, root);
        Assert.Equal("permission removed", closed.GetProperty("metadata").GetProperty("reason").GetString());
        Assert.False(closed.GetProperty("metadata").TryGetProperty("exitCode", out _));
    }

    [Fact]
    public async Task MissingShellExecute_ClosesWith4403Forbidden()
    {
        await using var h = await StartAsync();
        var root = await h.RootTokenAsync();
        var plain = await h.UserWithPermissionsAsync(root, "no-shell", "commands:execute");
        var close = await CloseOfAsync(h.Host, $"{Path}?ticket={await h.TicketAsync(plain)}");
        Assert.Equal(4403, close.Code);
        Assert.Equal("forbidden", close.Reason);
        Assert.Equal("login required", (await CloseOfAsync(h.Host, Path)).Reason);
    }

    private static async Task<JsonElement> LatestShellClosedAsync(UsersTestHost h, string token)
    {
        JsonElement closed = default;
        await ShellSecurityTests.WaitUntilAsync(() =>
        {
            var list = UsersTestHost.ReadJsonAsync(h.GetAsync("audit?event=shell_closed", token).Result).Result;
            if (list.GetArrayLength() == 0) return false;
            closed = list[0];
            return true;
        });
        return closed;
    }

    [Fact]
    public async Task DisablingTheUser_ClosesTheShellWith4401()
    {
        await using var h = await StartAsync();
        var root = await h.RootTokenAsync();
        var userToken = await h.UserWithPermissionsAsync(root, "to-disable", "shell:execute");
        var socket = await OpenAsync(h, userToken);

        Assert.Equal(200, (int)(await h.PutAsync("users/to-disable", root, new { disabled = true })).StatusCode);
        Assert.Equal(4401, (await ReadUntilCloseAsync(socket, 10000)).Code);
    }

    [Fact]
    public async Task SessionExpiry_IsDetectedByThePeriodicCheck()
    {
        await using var h = await StartAsync();
        var root = await h.RootTokenAsync();
        var socket = await OpenAsync(h, root);

        h.Time.Advance(TimeSpan.FromHours(13)); // beyond idle and absolute lifetime
        var close = await ReadUntilCloseAsync(socket, 10000);
        Assert.Equal(4401, close.Code);
        Assert.Equal("session ended", close.Reason);
    }

    [Fact]
    public async Task ShellStdin_CountsAsSessionActivity()
    {
        await using var h = await StartAsync();
        var root = await h.RootTokenAsync();
        var socket = await OpenAsync(h, root);

        // Shell input with no HTTP request touches the session (the first touch is immediate,
        // later ones at most every 30 s): 20 + 20 minutes stays within the 30 minute idle timeout.
        h.Time.Advance(TimeSpan.FromMinutes(20));
        await SendStdinAsync(socket, ":\n");
        await Task.Delay(500); // > check interval: the re-check touches lastActivity
        h.Time.Advance(TimeSpan.FromMinutes(20));
        await Task.Delay(500);
        Assert.Equal(WebSocketState.Open, socket.State);

        // Re-checks alone never extend the session.
        h.Time.Advance(TimeSpan.FromMinutes(20));
        var close = await ReadUntilCloseAsync(socket, 10000);
        Assert.Equal(4401, close.Code);
    }

    [Fact]
    public async Task PerUserLimit_ClosesWith4429()
    {
        await using var h = await StartAsync(o => o.MaxSessionsPerUser = 1);
        var root = await h.RootTokenAsync();
        var first = await OpenAsync(h, root);

        var second = await CloseOfAsync(h.Host, $"{Path}?ticket={await h.TicketAsync(root)}");
        Assert.Equal(4429, second.Code);

        // Another user is not affected.
        var other = await h.UserWithPermissionsAsync(root, "other-sheller", "shell:execute");
        var third = await OpenAsync(h, other);
        first.Abort();
        third.Abort();
    }

    [Fact]
    public async Task Audit_RecordsOpenedAndClosed_WithoutCommandText()
    {
        await using var h = await StartAsync();
        var (root, session) = await h.LoginAsync("root", "root");
        var close = await CloseOfAsync(h.Host,
            $"{Path}?cmd={Uri.EscapeDataString("echo audit-secret-xyz")}&ticket={await h.TicketAsync(root)}");
        Assert.Equal(0, close.ExitCode);

        JsonElement closed = default;
        await ShellSecurityTests.WaitUntilAsync(() =>
        {
            var list = UsersTestHost.ReadJsonAsync(h.GetAsync("audit?event=shell_closed", root).Result).Result;
            if (list.GetArrayLength() == 0) return false;
            closed = list[0];
            return true;
        });
        var opened = (await UsersTestHost.ReadJsonAsync(await h.GetAsync("audit?event=shell_opened", root)))[0];

        var userId = session.GetProperty("user").GetProperty("id").GetString();
        var sessionId = session.GetProperty("id").GetString();
        foreach (var entry in new[] { opened, closed })
        {
            Assert.Equal(userId, entry.GetProperty("userId").GetString());
            Assert.Equal(userId, entry.GetProperty("actorId").GetString());
            var meta = entry.GetProperty("metadata");
            Assert.Equal("command", meta.GetProperty("mode").GetString());
            Assert.Equal(sessionId, meta.GetProperty("sessionId").GetString());
        }
        Assert.False(opened.GetProperty("metadata").TryGetProperty("durationMs", out _));
        Assert.True(closed.GetProperty("metadata").GetProperty("durationMs").GetInt64() >= 0);
        Assert.Equal(0, closed.GetProperty("metadata").GetProperty("exitCode").GetInt32());
        Assert.Equal("exited", closed.GetProperty("metadata").GetProperty("reason").GetString());

        var all = await (await h.GetAsync("audit", root)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("audit-secret-xyz", all);
    }

    [Fact]
    public async Task Audit_InteractiveShellClosedBySessionEnd()
    {
        await using var h = await StartAsync();
        var root = await h.RootTokenAsync();
        var socket = await OpenAsync(h, root);
        await SendStdinAsync(socket, "echo typed-secret-123\n");
        await Task.Delay(200);

        var admin = await h.RootTokenAsync();
        await h.DeleteAsync("sessions/current", root);
        Assert.Equal(4401, (await ReadUntilCloseAsync(socket, 10000)).Code);

        JsonElement closed = default;
        await ShellSecurityTests.WaitUntilAsync(() =>
        {
            var list = UsersTestHost.ReadJsonAsync(h.GetAsync("audit?event=shell_closed", admin).Result).Result;
            if (list.GetArrayLength() == 0) return false;
            closed = list[0];
            return true;
        });
        Assert.Equal("interactive", closed.GetProperty("metadata").GetProperty("mode").GetString());
        Assert.Equal("session ended", closed.GetProperty("metadata").GetProperty("reason").GetString());
        var all = await (await h.GetAsync("audit", admin)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("typed-secret-123", all);
    }
}
