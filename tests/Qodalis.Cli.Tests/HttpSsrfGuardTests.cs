using System.Net;
using System.Net.Sockets;
using System.Text;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Security;
using Qodalis.Cli.Server.Processors;

namespace Qodalis.Cli.Tests;

public class HttpSsrfGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    public void PrivateAndLocalAddressesAreBlocked(string ip)
    {
        Assert.True(CliOutboundRequestGuard.IsPrivateOrLocal(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void PublicAddressesAreAllowed(string ip)
    {
        Assert.False(CliOutboundRequestGuard.IsPrivateOrLocal(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/", "169.254.169.254", "169.254.169.254")]
    [InlineData("http://127.0.0.1:1/", "127.0.0.1", "127.0.0.1")]
    [InlineData("http://[::1]:1/", "[::1]", "::1")]
    [InlineData("http://[::ffff:127.0.0.1]:1/", "[::ffff:127.0.0.1]", "127.0.0.1")]
    public async Task HttpGetRefusesPrivateDestinations(string url, string host, string ip)
    {
        var result = await RunGetAsync(url, options: null);

        Assert.StartsWith($"Blocked: {host} resolves to a private or local address (", result);
        Assert.Contains(ip, result);
        Assert.EndsWith("Set AllowPrivateNetworks to allow it.", result);
    }

    [Fact]
    public async Task HttpGetRefusesHostNamesResolvingToLoopback()
    {
        var result = await RunGetAsync("http://localhost:1/", options: null);

        Assert.StartsWith("Blocked: localhost resolves to a private or local address (", result);
    }

    [Fact]
    public async Task HttpGetRefusesNonHttpSchemes()
    {
        var result = await RunGetAsync("file:///etc/passwd", options: null);

        Assert.Equal("Error: only http:// and https:// URLs are supported", result);
    }

    [Fact]
    public async Task GuardedHandlerRefusesTheMetadataAddress()
    {
        // The check runs in ConnectCallback, so it applies to every connection the handler opens,
        // including redirect hops.
        var handler = CliOutboundRequestGuard.CreateHandler(allowPrivateNetworks: false);
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.GetAsync("http://169.254.169.254/latest/meta-data/"));
        Assert.True(ex is CliPrivateNetworkBlockedException || ex.InnerException is CliPrivateNetworkBlockedException);
    }

    [Fact]
    public async Task AllowPrivateNetworksRestoresAccess()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = ServeOnceAsync(listener);

        var result = await RunGetAsync($"http://127.0.0.1:{port}/", new CliServerOptions { AllowPrivateNetworks = true });
        await serve;

        Assert.StartsWith("Status: 200", result);
        Assert.Contains("hello", result);
    }

    [Fact]
    public async Task GuardIsOnByDefaultForADiResolvedCommand()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var result = await RunGetAsync($"http://127.0.0.1:{port}/", new CliServerOptions());

        Assert.StartsWith("Blocked: 127.0.0.1 resolves to a private or local address (127.0.0.1).", result);
        Assert.False(listener.Pending());
    }

    private static async Task<string> RunGetAsync(string url, CliServerOptions? options)
    {
        var http = new CliHttpCommandProcessor(options);
        var get = http.Processors!.OfType<CliHttpGetProcessor>().Single();
        return await get.HandleAsync(new CliProcessCommand
        {
            Command = "http",
            Value = url,
            Args = new Dictionary<string, object>(),
        });
    }

    private static async Task ServeOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var buffer = new byte[4096];
        await stream.ReadAsync(buffer);
        var body = "hello";
        var response = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
        await stream.FlushAsync();
    }
}
