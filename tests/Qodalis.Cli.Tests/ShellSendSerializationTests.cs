using System.Net.WebSockets;
using System.Reflection;
using Qodalis.Cli.Services;
using Xunit;

namespace Qodalis.Cli.Tests;

/// <summary>Output, exit and close frames of one shell socket must never be sent concurrently.</summary>
public class ShellSendSerializationTests
{
    private sealed class OverlapDetectingSocket : WebSocket
    {
        private int _inFlight;
        public bool Overlapped { get; private set; }
        public int Sent;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Track();
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool e, CancellationToken c) => Track();

        private async Task Track()
        {
            if (Interlocked.Increment(ref _inFlight) > 1) Overlapped = true;
            await Task.Delay(5);
            Interlocked.Decrement(ref _inFlight);
            Interlocked.Increment(ref Sent);
        }
    }

    [Fact]
    public async Task ConcurrentSendsOnOneSocketAreSerialized()
    {
        var send = typeof(ShellSessionManager).GetMethod("SendJsonAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var close = typeof(ShellSessionManager).GetMethod("SendCloseFrameAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var socket = new OverlapDetectingSocket();

        var tasks = Enumerable.Range(0, 10)
            .Select(i => (Task)send.Invoke(null, [socket, new { type = "stdout", data = i.ToString() }, CancellationToken.None])!)
            .Append((Task)close.Invoke(null, [socket, 1000, "done"])!)
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(11, socket.Sent);
        Assert.False(socket.Overlapped);
    }
}
