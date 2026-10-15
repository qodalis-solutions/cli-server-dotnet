using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests;

/// <summary>
/// Events and logs sockets authenticated with a stored session are bound to it: when the session
/// ends the socket is closed with 4401 <c>session ended</c>. Sockets without a session are not checked.
/// </summary>
public class SessionBoundSocketsTests
{
    /// <summary>An open socket that records sends and the close frame; a close ends the receive loop.</summary>
    private sealed class RecordingWebSocket : WebSocket
    {
        private readonly TaskCompletionSource _closeRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        private int _sending;

        public List<string> Sent { get; } = [];
        public int Overlaps;
        public WebSocketCloseStatus? SentCloseStatus { get; private set; }
        public string? SentCloseDescription { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => SentCloseStatus;
        public override string? CloseStatusDescription => SentCloseDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _sending) == 1) Interlocked.Increment(ref Overlaps);
            SentCloseStatus = closeStatus;
            SentCloseDescription = statusDescription;
            _state = WebSocketState.CloseSent;
            _closeRequested.TrySetResult();
            return Task.CompletedTask;
        }

        public override void Dispose() { }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await _closeRequested.Task.WaitAsync(cancellationToken);
            _state = WebSocketState.Closed; // the client answered the close
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, SentCloseStatus, SentCloseDescription);
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _sending, 1) == 1) Interlocked.Increment(ref Overlaps);
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

    private static CliPrincipal Principal(string userId, string? sessionId) => new(userId, userId, [], sessionId);

    [Fact]
    public async Task EventSocket_SessionEnded_ClosesWith4401AndStopsDelivery()
    {
        using var manager = new CliEventSocketManager(NullLogger<CliEventSocketManager>.Instance);
        var ended = new RecordingWebSocket();
        var alive = new RecordingWebSocket();
        using var cts = new CancellationTokenSource();
        var c1 = manager.HandleConnectionAsync(ended, cts.Token, "127.0.0.1", Principal("u1", "s-ended"));
        var c2 = manager.HandleConnectionAsync(alive, cts.Token, "127.0.0.1", Principal("u2", "s-alive"));
        await WaitForAsync(() => ended.Sent.Count == 1 && alive.Sent.Count == 1);

        await manager.CloseEndedSessionsAsync(p => Task.FromResult(p.SessionId != "s-ended"));

        Assert.Equal((WebSocketCloseStatus)4401, ended.SentCloseStatus);
        Assert.Equal("session ended", ended.SentCloseDescription);
        Assert.Null(alive.SentCloseStatus);
        await c1;

        await manager.BroadcastMessageAsync("{\"type\":\"after\"}");
        Assert.Single(ended.Sent);
        Assert.Equal(2, alive.Sent.Count);
        Assert.DoesNotContain(manager.GetClients(), c => c.UserId == "u1");

        cts.Cancel();
        await c2;
    }

    [Fact]
    public async Task EventSocket_WithoutStoredSession_IsNotChecked()
    {
        using var manager = new CliEventSocketManager(NullLogger<CliEventSocketManager>.Instance);
        var anonymous = new RecordingWebSocket();
        var adapter = new RecordingWebSocket();
        using var cts = new CancellationTokenSource();
        var c1 = manager.HandleConnectionAsync(anonymous, cts.Token, "127.0.0.1");
        var c2 = manager.HandleConnectionAsync(adapter, cts.Token, "127.0.0.1", Principal("ext", null));
        await WaitForAsync(() => anonymous.Sent.Count == 1 && adapter.Sent.Count == 1);

        var checks = 0;
        await manager.CloseEndedSessionsAsync(_ => { checks++; return Task.FromResult(false); });

        Assert.Equal(0, checks);
        Assert.Null(anonymous.SentCloseStatus);
        Assert.Null(adapter.SentCloseStatus);

        cts.Cancel();
        await Task.WhenAll(c1, c2);
    }

    [Fact]
    public async Task EventSocket_FailingCheck_ClosesTheSocket()
    {
        using var manager = new CliEventSocketManager(NullLogger<CliEventSocketManager>.Instance);
        var socket = new RecordingWebSocket();
        using var cts = new CancellationTokenSource();
        var connection = manager.HandleConnectionAsync(socket, cts.Token, "127.0.0.1", Principal("u1", "s1"));
        await WaitForAsync(() => socket.Sent.Count == 1);

        await manager.CloseEndedSessionsAsync(_ => throw new InvalidOperationException("store down"));

        Assert.Equal((WebSocketCloseStatus)4401, socket.SentCloseStatus);
        await connection;
    }

    [Fact]
    public async Task EventSocket_CloseDoesNotOverlapSends()
    {
        using var manager = new CliEventSocketManager(NullLogger<CliEventSocketManager>.Instance);
        var socket = new RecordingWebSocket();
        using var cts = new CancellationTokenSource();
        var connection = manager.HandleConnectionAsync(socket, cts.Token, "127.0.0.1", Principal("u1", "s1"));
        await WaitForAsync(() => socket.Sent.Count == 1);

        var sends = Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => manager.SendToUserAsync("u1", $"{{\"type\":\"m-{i}\"}}")))
            .ToList();
        sends.Add(Task.Run(() => manager.CloseEndedSessionsAsync(_ => Task.FromResult(false))));
        await Task.WhenAll(sends);

        Assert.Equal(0, socket.Overlaps);
        Assert.Equal((WebSocketCloseStatus)4401, socket.SentCloseStatus);
        await connection;
    }

    [Fact]
    public async Task LogSocket_SessionEnded_ClosesWith4401AndStopsDelivery()
    {
        using var manager = new CliLogSocketManager();
        var ended = new RecordingWebSocket();
        var alive = new RecordingWebSocket();
        var anonymous = new RecordingWebSocket();
        using var cts = new CancellationTokenSource();
        var c1 = manager.HandleConnectionAsync(ended, null, cts.Token, Principal("u1", "s-ended"));
        var c2 = manager.HandleConnectionAsync(alive, null, cts.Token, Principal("u2", "s-alive"));
        var c3 = manager.HandleConnectionAsync(anonymous, null, cts.Token);
        await WaitForAsync(() => ended.Sent.Count == 1 && alive.Sent.Count == 1 && anonymous.Sent.Count == 1);

        await manager.CloseEndedSessionsAsync(p => Task.FromResult(p.SessionId != "s-ended"));

        Assert.Equal((WebSocketCloseStatus)4401, ended.SentCloseStatus);
        Assert.Equal("session ended", ended.SentCloseDescription);
        Assert.Null(alive.SentCloseStatus);
        Assert.Null(anonymous.SentCloseStatus);
        await c1;

        await manager.BroadcastLogAsync("information", "after", "test");
        Assert.Single(ended.Sent);
        Assert.Equal(2, alive.Sent.Count);

        cts.Cancel();
        await Task.WhenAll(c2, c3);
    }
}
