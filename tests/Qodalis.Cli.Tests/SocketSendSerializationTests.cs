using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests;

/// <summary>
/// .NET WebSockets reject a send while another send on the same socket is in progress; the socket
/// managers must serialize sends per connection so concurrent broadcasts lose no message.
/// </summary>
public class SocketSendSerializationTests
{
    /// <summary>An open socket that, like the real ones, throws on overlapping sends.</summary>
    private sealed class StrictSendWebSocket : WebSocket
    {
        private int _sending;
        private WebSocketState _state = WebSocketState.Open;

        public List<string> Sent { get; } = [];
        public int Overlaps;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() { }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _sending, 1) == 1)
            {
                Interlocked.Increment(ref Overlaps);
                throw new InvalidOperationException("There is already one outstanding 'SendAsync' call for this WebSocket instance.");
            }
            try
            {
                await Task.Delay(2, cancellationToken);
                lock (Sent) Sent.Add(Encoding.UTF8.GetString(buffer));
            }
            finally
            {
                Volatile.Write(ref _sending, 0);
            }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("condition not met in time");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task EventSockets_ConcurrentBroadcastAndUserSends_AreSerialized()
    {
        using var manager = new CliEventSocketManager(NullLogger<CliEventSocketManager>.Instance);
        var socket = new StrictSendWebSocket();
        using var cts = new CancellationTokenSource();
        var connection = manager.HandleConnectionAsync(socket, cts.Token, "127.0.0.1",
            new CliPrincipal("u1", "alice", []));
        await WaitForAsync(() => socket.Sent.Count == 1); // "connected"

        var sends = Enumerable.Range(0, 20).SelectMany(i => new[]
        {
            Task.Run(() => manager.BroadcastMessageAsync($"{{\"type\":\"broadcast-{i}\"}}")),
            Task.Run(() => manager.SendToUserAsync("u1", $"{{\"type\":\"session-kicked-{i}\"}}")),
        });
        await Task.WhenAll(sends);

        Assert.Equal(0, socket.Overlaps);
        Assert.Equal(41, socket.Sent.Count);
        for (var i = 0; i < 20; i++) Assert.Contains($"{{\"type\":\"session-kicked-{i}\"}}", socket.Sent);

        cts.Cancel();
        await connection;
    }

    [Fact]
    public async Task LogSockets_ConcurrentBroadcasts_AreSerialized()
    {
        using var manager = new CliLogSocketManager();
        var socket = new StrictSendWebSocket();
        using var cts = new CancellationTokenSource();
        var connection = manager.HandleConnectionAsync(socket, null, cts.Token);
        await WaitForAsync(() => socket.Sent.Count == 1);

        await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(i => Task.Run(() => manager.BroadcastLogAsync("information", $"message {i}", "test"))));

        Assert.Equal(0, socket.Overlaps);
        Assert.Equal(41, socket.Sent.Count);

        cts.Cancel();
        await connection;
    }
}
