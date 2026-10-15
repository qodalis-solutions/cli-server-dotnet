using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Tests.Helpers;

/// <summary>Result of reading a shell socket until the server closes it.</summary>
public sealed record ShellClose(int? Code, string? Reason, string Stdout, IReadOnlyList<JsonElement> Messages, int? ExitCode)
{
    public bool GotReady => Messages.Any(m => m.GetProperty("type").GetString() == "ready");
}

/// <summary>Real WebSocket upgrades against a <see cref="TestServer"/> for the shell endpoints.</summary>
public static class ShellTestClient
{
    public const string RemoteHeader = "X-Test-Remote";

    public static Task<WebSocket> ConnectAsync(IHost host, string pathAndQuery, Action<HttpRequest>? configure = null)
    {
        var client = host.GetTestServer().CreateWebSocketClient();
        if (configure != null) client.ConfigureRequest = configure;
        return client.ConnectAsync(new Uri("ws://localhost" + pathAndQuery), CancellationToken.None);
    }

    /// <summary>Connects and reads until the server closes the socket.</summary>
    public static async Task<ShellClose> CloseOfAsync(IHost host, string pathAndQuery, Action<HttpRequest>? configure = null,
        int timeoutMs = 15000)
    {
        using var socket = await ConnectAsync(host, pathAndQuery, configure);
        return await ReadUntilCloseAsync(socket, timeoutMs);
    }

    /// <summary>Reads messages until the server closes the socket (or the timeout elapses: Code = null).</summary>
    public static async Task<ShellClose> ReadUntilCloseAsync(WebSocket socket, int timeoutMs = 15000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var messages = new List<JsonElement>();
        var stdout = new StringBuilder();
        int? exit = null;
        var buffer = new byte[16384];
        var current = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    }
                    catch
                    {
                        // already closed
                    }
                    return new ShellClose((int?)result.CloseStatus, result.CloseStatusDescription, stdout.ToString(), messages, exit);
                }

                current.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var msg = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(current.ToArray()));
                current.SetLength(0);
                messages.Add(msg);
                switch (msg.GetProperty("type").GetString())
                {
                    case "stdout": stdout.Append(msg.GetProperty("data").GetString()); break;
                    case "exit": exit = msg.GetProperty("code").GetInt32(); break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new ShellClose(null, null, stdout.ToString(), messages, exit);
        }
    }

    /// <summary>Reads until the <c>ready</c> message (fails on close or timeout).</summary>
    public static async Task ExpectReadyAsync(WebSocket socket, int timeoutMs = 15000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[16384];
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            Assert.True(result.MessageType != WebSocketMessageType.Close,
                $"shell closed before ready: {(int?)result.CloseStatus} {result.CloseStatusDescription}");
            var msg = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (msg.GetProperty("type").GetString() == "ready") return;
        }
    }

    /// <summary>Reads stdout until <paramref name="done"/> holds for the accumulated text (fails on close or timeout).</summary>
    public static async Task<string> ReadStdoutUntilAsync(WebSocket socket, Func<string, bool> done, int timeoutMs = 15000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var stdout = new StringBuilder();
        var buffer = new byte[16384];
        var current = new MemoryStream();
        while (!done(stdout.ToString()))
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            Assert.True(result.MessageType != WebSocketMessageType.Close,
                $"shell closed early: {(int?)result.CloseStatus} {result.CloseStatusDescription}; stdout: {stdout}");
            current.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var msg = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(current.ToArray()));
            current.SetLength(0);
            if (msg.GetProperty("type").GetString() == "stdout") stdout.Append(msg.GetProperty("data").GetString());
        }
        return stdout.ToString();
    }

    public static Task SendStdinAsync(WebSocket socket, string data) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "stdin", data })),
            WebSocketMessageType.Text, true, CancellationToken.None);

    public static Action<HttpRequest> FromAddress(string address) => r => r.Headers[RemoteHeader] = address;
}

/// <summary>Test host for the CLI server WITHOUT the users plugin.</summary>
public sealed class ShellTestHost : IAsyncDisposable
{
    private ShellTestHost(IHost host, CapturingLoggerProvider logs)
    {
        Host = host;
        Client = host.GetTestClient();
        Logs = logs;
    }

    public IHost Host { get; }
    public HttpClient Client { get; }
    public CapturingLoggerProvider Logs { get; }

    public static async Task<ShellTestHost> StartAsync(Action<CliBuilder>? configureCli = null)
    {
        var logs = new CapturingLoggerProvider();
        var builder = new HostBuilder()
            .ConfigureLogging(l => l.AddProvider(logs).SetMinimumLevel(LogLevel.Debug)
                // ASP.NET Core's own request logs (Information) include query strings; hosts keep
                // "Microsoft.AspNetCore" at Warning. The assertions cover the library's logs.
                .AddFilter("Microsoft.AspNetCore", LogLevel.Warning))
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services => services.AddControllers().AddCli(cli => configureCli?.Invoke(cli)));
                webHost.Configure(app =>
                {
                    // Lets tests simulate distinct client addresses.
                    app.Use((ctx, next) =>
                    {
                        var remote = ctx.Request.Headers[ShellTestClient.RemoteHeader].FirstOrDefault();
                        if (remote != null) ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
                        return next();
                    });
                    app.UseWebSockets();
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapControllers());
                    app.UseCli();
                });
            });
        return new ShellTestHost(await builder.StartAsync(), logs);
    }

    public async Task<JsonElement> CapabilitiesAsync()
    {
        var res = await Client.GetAsync("/api/v1/qcli/capabilities");
        res.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await res.Content.ReadAsStringAsync());
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.StopAsync();
        Host.Dispose();
    }
}
