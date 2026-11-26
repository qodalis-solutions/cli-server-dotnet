using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.Restart;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Restart;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Config;

namespace Qodalis.Cli.Tests;

/// <summary>
/// A server without sign-in (the admin plugin's password sign-in, no users plugin): a caller who is not signed in may
/// not install server or dashboard plugins nor change settings (through <c>/execute</c>, MCP or the restart route), the
/// static admin's token may, reads stay open, and <c>AllowAnonymousAdmin</c> opts back in with a startup warning.
/// </summary>
public sealed class AnonymousAdminTests : IAsyncDisposable
{
    private readonly MarketplaceTestServers _servers = new();

    public ValueTask DisposeAsync() => _servers.DisposeAsync();

    private sealed class LogList : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }

    [Theory]
    [InlineData("admin plugins install fixture", "plugins:install")]
    [InlineData("admin plugins remove fixture", "plugins:install")]
    [InlineData("admin dashboard install @acme/cli-dashboard-tickets", "dashboard:install")]
    [InlineData("config set logs.buffer.minLevel DEBUG", "config:write")]
    public async Task WithoutSignIn_InstallingAndChangingSettingsAreRefused(string line, string permission)
    {
        var h = await _servers.StartAsync();
        var (status, _, body) = await h.RunAnonymous(line);
        Assert.Equal(403, status);
        Assert.StartsWith($"sign-in required: {permission}", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WithoutSignIn_ReadsStayAllowed()
    {
        var h = await _servers.StartAsync();
        Assert.Equal(200, (await h.RunAnonymous("config list")).Status);
        Assert.Equal(200, (await h.RunAnonymous("admin plugins installed")).Status);
        Assert.Equal(200, (await h.RunAnonymous("admin dashboard")).Status);
    }

    [Fact]
    public async Task TheStaticAdminsToken_MayInstallAndChangeSettings()
    {
        var h = await _servers.StartAsync();
        var registry = h.Service<ICliCommandRegistry>();
        var set = await ConfigCommands.RunAsync(h.Client, registry, "config set logs.buffer.minLevel DEBUG", h.Token);
        Assert.Equal(200, set.Status);
        Assert.DoesNotContain("Error", set.Text);
        // reaches the marketplace (none is published here) instead of being refused
        var install = await ConfigCommands.RunAsync(h.Client, registry, "admin plugins install fixture", h.Token);
        Assert.Equal(200, install.Status);
        Assert.DoesNotContain("sign-in required", install.Text);
        // a token that is not the dashboard's is no sign-in
        Assert.Equal(403, (await ConfigCommands.RunAsync(h.Client, registry, "admin plugins install fixture", "not-a-jwt")).Status);
    }

    [Fact]
    public async Task AllowAnonymousAdmin_LetsAnonymousCallersInstall_WithAStartupWarning()
    {
        var logs = new LogList();
        var h = await _servers.StartAsync(configure: cli =>
        {
            cli.AllowAnonymousAdmin();
            cli.Services.AddSingleton<ILoggerProvider>(logs);
        });
        var set = await h.RunAnonymous("config set logs.buffer.minLevel DEBUG");
        Assert.Equal(200, set.Status);
        Assert.DoesNotContain("Error", set.Text);
        Assert.NotEqual(403, (await h.RunAnonymous("admin plugins install fixture")).Status);
        lock (logs.Lines)
            Assert.Contains(logs.Lines, l => l.StartsWith("Warning: WARNING: AllowAnonymousAdmin is on") &&
                                             l.Contains("plugins:install, dashboard:install, config:write"));
    }

    [Fact]
    public async Task Mcp_WithoutSignIn_HidesConfigSet_UnlessAllowAnonymousAdmin()
    {
        await using (var host = await ConfigTestHost.StartAsync(cli =>
                     {
                         cli.AllowAnonymousAdmin(false);
                         cli.AddMcp();
                     }))
        {
            var names = await Mcp.ToolNamesAsync(host.Client);
            Assert.Contains("config_list", names);
            Assert.DoesNotContain("config_set", names);
            var (_, message) = await Mcp.ErrorAsync(host.Client, "tools/call",
                new { name = "config_set", arguments = new { provider = "x", section = "y", changes = new { } } });
            Assert.Equal("Unknown tool: config_set", message);
        }
        await using (var open = await ConfigTestHost.StartAsync(cli => cli.AddMcp()))
            Assert.Contains("config_set", await Mcp.ToolNamesAsync(open.Client));
    }

    [Fact]
    public async Task ConfigContext_WithoutSignIn_ReadsButDoesNotWrite()
    {
        var closed = new CliServerOptions();
        Assert.True(CliConfigContexts.Anonymous(closed, "api").HasPermission("config:read"));
        Assert.False(CliConfigContexts.Anonymous(closed, "api").HasPermission("config:write"));
        Assert.True(CliConfigContexts.Anonymous(new CliServerOptions { AllowAnonymousAdmin = true }, "api").HasPermission("config:write"));
        Assert.False(CliConfigContexts.Anonymous(new CliServerOptions { RequireAuthentication = true }, "api").HasPermission("config:read"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Restart_IsBehindTheDashboardSignIn_AndTheStaticAdminMayRestart()
    {
        var runtime = new FakeRestartRuntime();
        var h = await _servers.StartAsync(configure: cli => cli.ConfigureRestart(o =>
        {
            o.Mode = CliServerRestartModes.Exit;
            o.Runtime = runtime;
            // even with AllowAnonymous, the admin plugin's sign-in guards the route
            o.AllowAnonymous = true;
        }));
        Assert.Equal(401, (int)(await h.Client.PostAsync("/api/v1/qcli/server/restart", null)).StatusCode);
        Assert.Equal(401, (int)(await h.Client.GetAsync("/api/v1/qcli/server/restart")).StatusCode);
        Assert.Empty(runtime.Calls);

        Assert.Equal(200, (int)(await h.Get("/api/v1/qcli/server/restart")).StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/qcli/server/restart") { Content = JsonContent.Create(new { reason = "test" }) };
        req.Headers.Authorization = new("Bearer", h.Token);
        var res = await h.Client.SendAsync(req);
        Assert.Equal(202, (int)res.StatusCode);
        await h.Service<CliServerRestart>().Completion!;
        Assert.Contains("stop 75", runtime.Calls);
    }

    [Fact]
    public async Task AdminRestartCommand_WithTheStaticAdminsToken_Restarts()
    {
        var runtime = new FakeRestartRuntime();
        var h = await _servers.StartAsync(configure: cli => cli.ConfigureRestart(o =>
        {
            o.Mode = CliServerRestartModes.Exit;
            o.Runtime = runtime;
        }));
        var registry = h.Service<ICliCommandRegistry>();
        Assert.StartsWith("Error: restart needs sign-in", (await h.RunAnonymous("admin restart --yes")).Text);
        Assert.StartsWith("Restarting the server", (await ConfigCommands.RunAsync(h.Client, registry, "admin restart --yes", h.Token)).Text);
        await h.Service<CliServerRestart>().Completion!;
    }
}
