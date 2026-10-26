using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Security;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

public class HostAllowlistTests
{
    // ---------------------------------------------------------------------
    // Matching
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST:8046")]
    [InlineData("app.localhost")]
    [InlineData("localhost.")]
    [InlineData("127.0.0.1:8047")]
    [InlineData("192.168.1.20")]
    [InlineData("[::1]:8048")]
    [InlineData("[fe80::1]")]
    public void LocalNamesAndIpLiteralsAreAlwaysAllowed(string host)
    {
        Assert.True(new CliServerOptions().IsHostAllowed(host));
    }

    [Theory]
    [InlineData("attacker.example")]
    [InlineData("attacker.example:8046")]
    [InlineData("localhost.attacker.example")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherHostsAreRejectedByDefault(string? host)
    {
        Assert.False(new CliServerOptions().IsHostAllowed(host));
    }

    [Theory]
    [InlineData("cli.example.com", true)]
    [InlineData("CLI.Example.com:443", true)]
    [InlineData("a.corp.example", true)]
    [InlineData("a.b.corp.example", true)]
    [InlineData("corp.example", false)]
    [InlineData("evilcorp.example", false)]
    [InlineData("example.com", false)]
    public void AllowedHostsMatchExactlyOrByWildcard(string host, bool expected)
    {
        var options = new CliServerOptions { AllowedHosts = ["cli.example.com", "*.corp.example"] };
        Assert.Equal(expected, options.IsHostAllowed(host));
    }

    [Fact]
    public void StarDisablesTheCheck()
    {
        var options = new CliServerOptions { AllowedHosts = ["*"] };
        Assert.True(options.IsHostAllowed("anything.example"));
    }

    [Fact]
    public void EnforcedOnlyWhenUnprotectedUnlessConfigured()
    {
        Assert.True(new CliServerOptions().EnforcesHostCheck);
        Assert.False(new CliServerOptions { RequireAuthentication = true }.EnforcesHostCheck);
        Assert.True(new CliServerOptions { RequireAuthentication = true, AllowedHosts = [] }.EnforcesHostCheck);
    }

    // ---------------------------------------------------------------------
    // Unprotected server (no users plugin): enforced by default
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("/api/v1/qcli/commands")]
    [InlineData("/api/qcli/versions")]
    [InlineData("/api/qcli/fs/ls?path=/")]
    public async Task RebindingHostIsRejectedOnRestRoutes(string path)
    {
        await using var host = await StartAsync();
        using var client = Client(host, "http://attacker.example/");

        var res = await client.GetAsync(path);

        Assert.Equal(421, (int)res.StatusCode);
        Assert.Contains("misdirected request: host not allowed", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RebindingHostCannotExecuteCommands()
    {
        await using var host = await StartAsync();
        using var client = Client(host, "http://attacker.example/");

        var res = await client.PostAsync("/api/v1/qcli/execute",
            new StringContent("{\"command\":\"echo\",\"value\":\"hi\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(421, (int)res.StatusCode);
    }

    [Fact]
    public async Task RebindingHostIsRejectedOnWebSockets()
    {
        await using var host = await StartAsync();
        var ws = host.Host.GetTestServer().CreateWebSocketClient();

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => ws.ConnectAsync(new Uri("ws://attacker.example/ws/v1/qcli/events"), CancellationToken.None));
        Assert.Contains("421", ex.Message);
    }

    [Fact]
    public async Task RebindingHostIsRejectedOnMcp()
    {
        await using var host = await StartAsync(cli => cli.AddMcp());
        using var client = Client(host, "http://attacker.example/");

        var res = await client.PostAsync(Mcp.Path, Mcp.Body(Mcp.Request("tools/list")));

        Assert.Equal(421, (int)res.StatusCode);
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://127.0.0.1:8046/")]
    [InlineData("http://[::1]/")]
    public async Task LocalHostsStillWork(string baseAddress)
    {
        await using var host = await StartAsync();
        using var client = Client(host, baseAddress);

        var res = await client.GetAsync("/api/v1/qcli/commands");

        Assert.Equal(200, (int)res.StatusCode);
    }

    [Fact]
    public async Task ConfiguredHostIsAllowed()
    {
        await using var host = await StartAsync(cli => cli.SetAllowedHosts("cli.example.com"));
        using var allowed = Client(host, "http://cli.example.com/");
        using var other = Client(host, "http://attacker.example/");

        Assert.Equal(200, (int)(await allowed.GetAsync("/api/v1/qcli/commands")).StatusCode);
        Assert.Equal(421, (int)(await other.GetAsync("/api/v1/qcli/commands")).StatusCode);
    }

    [Fact]
    public async Task WildcardDisablesTheCheck()
    {
        await using var host = await StartAsync(cli => cli.SetAllowedHosts("*"));
        using var client = Client(host, "http://attacker.example/");

        Assert.Equal(200, (int)(await client.GetAsync("/api/v1/qcli/commands")).StatusCode);
    }

    [Fact]
    public async Task NonCliRoutesAreNotAffected()
    {
        await using var host = await StartAsync();
        using var client = Client(host, "http://attacker.example/");

        var res = await client.GetAsync("/not-a-cli-route");

        Assert.Equal(404, (int)res.StatusCode);
    }

    // ---------------------------------------------------------------------
    // Protected server (users plugin, ProtectServer): not enforced unless configured
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ProtectedServerDoesNotCheckHostByDefault()
    {
        await using var host = await UsersTestHost.StartAsync();
        using var client = Client(host.Host, "http://cli.internal.example/");

        var res = await client.GetAsync("/api/qcli/versions");

        Assert.Equal(200, (int)res.StatusCode);
    }

    [Fact]
    public async Task ProtectedServerChecksHostWhenConfigured()
    {
        await using var host = await UsersTestHost.StartAsync(configureCli: cli => cli.SetAllowedHosts("cli.example.com"));
        using var client = Client(host.Host, "http://attacker.example/");

        var res = await client.GetAsync("/api/qcli/versions");

        Assert.Equal(421, (int)res.StatusCode);
    }

    [Fact]
    public async Task UnprotectedUsersServerChecksHost()
    {
        await using var host = await UsersTestHost.StartAsync(o => o.ProtectServer = false);
        using var client = Client(host.Host, "http://attacker.example/");

        var res = await client.GetAsync("/api/v1/qcli/commands");

        Assert.Equal(421, (int)res.StatusCode);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static HttpClient Client(IHost host, string baseAddress)
    {
        var client = host.GetTestServer().CreateClient();
        client.BaseAddress = new Uri(baseAddress);
        return client;
    }

    private static HttpClient Client(McpPlainHost host, string baseAddress) => Client(host.Host, baseAddress);

    private static Task<McpPlainHost> StartAsync(Action<CliBuilder>? configure = null) =>
        McpPlainHost.StartAsync(cli =>
        {
            cli.AddProcessor<McpEchoProcessor>();
            cli.AddFileSystem(o => o.AllowedPaths.Add(Path.GetTempPath()));
            configure?.Invoke(cli);
        });
}
