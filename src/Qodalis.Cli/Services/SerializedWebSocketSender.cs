using System.Net.WebSockets;

namespace Qodalis.Cli.Services;

/// <summary>
/// A server-push WebSocket whose sends are serialized: .NET WebSockets allow only one outstanding
/// <see cref="WebSocket.SendAsync(ArraySegment{byte}, WebSocketMessageType, bool, CancellationToken)"/>
/// per socket, so concurrent broadcasts (and user-targeted messages such as <c>session-kicked</c>)
/// would otherwise fail and be lost. Dispose when the connection ends: sends still waiting return false.
/// </summary>
internal sealed class SerializedWebSocketSender : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _closed = new();

    public SerializedWebSocketSender(WebSocket socket) => Socket = socket;

    public WebSocket Socket { get; }

    /// <summary>
    /// Sends one text message after any send in progress. Returns false when the socket is not open
    /// or the connection ended while waiting; send failures propagate.
    /// </summary>
    public async Task<bool> SendTextAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        if (!await EnterAsync()) return false;
        try
        {
            if (Socket.State != WebSocketState.Open) return false;
            await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
            return true;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Sends a last text message, then closes the socket (no other send can interleave).</summary>
    public async Task SendAndCloseAsync(byte[] bytes, string reason)
    {
        if (!await EnterAsync()) return;
        try
        {
            if (Socket.State != WebSocketState.Open) return;
            await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Sends a close frame (no other send can interleave). Does not wait for the client's close:
    /// the connection's receive loop ends when it arrives.
    /// </summary>
    public async Task CloseAsync(int code, string reason)
    {
        if (!await EnterAsync()) return;
        try
        {
            if (Socket.State != WebSocketState.Open) return;
            await Socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, CancellationToken.None);
        }
        finally
        {
            Exit();
        }
    }

    private async Task<bool> EnterAsync()
    {
        try
        {
            await _gate.WaitAsync(_closed.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void Exit()
    {
        try { _gate.Release(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        // Cancel waiting senders first so none is left pending on a disposed semaphore.
        try { _closed.Cancel(); } catch (ObjectDisposedException) { return; }
        _gate.Dispose();
        _closed.Dispose();
    }
}
