using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Qodalis.Cli.Services;

/// <summary>
/// Holds metadata about a connected WebSocket client.
/// </summary>
public class CliWebSocketClientInfo
{
    /// <summary>Gets or sets the unique client identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the ISO 8601 timestamp when the client connected.</summary>
    public string ConnectedAt { get; set; } = string.Empty;

    /// <summary>Gets or sets the client's remote address.</summary>
    public string RemoteAddress { get; set; } = string.Empty;

    /// <summary>Gets or sets the connection type (e.g., "events").</summary>
    public string Type { get; set; } = "events";
}

/// <summary>
/// Manages WebSocket connections for server-push event broadcasting to connected clients.
/// </summary>
public class CliEventSocketManager : ICliEventSocketManager
{
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private readonly ConcurrentDictionary<string, CliWebSocketClientInfo> _clientInfo = new();

    /// <inheritdoc />
    public async Task HandleConnectionAsync(WebSocket socket, CancellationToken cancellationToken, string? remoteAddress = null)
    {
        var id = Guid.NewGuid().ToString();
        _clients.TryAdd(id, socket);
        _clientInfo.TryAdd(id, new CliWebSocketClientInfo
        {
            Id = id,
            ConnectedAt = DateTime.UtcNow.ToString("o"),
            RemoteAddress = remoteAddress ?? "unknown",
            Type = "events"
        });

        try
        {
            // Send connected event
            await SendAsync(socket, new { type = "connected" }, cancellationToken);

            // Keep alive — wait for client to close or cancellation
            var buffer = new byte[256];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down
        }
        catch (WebSocketException)
        {
            // Client disconnected
        }
        finally
        {
            _clients.TryRemove(id, out _);
            _clientInfo.TryRemove(id, out _);
        }
    }

    /// <inheritdoc />
    public async Task BroadcastMessageAsync(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var segment = new ArraySegment<byte>(bytes);
        var tasks = new List<Task>();

        foreach (var (_, socket) in _clients)
        {
            if (socket.State == WebSocketState.Open)
            {
                tasks.Add(socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None));
            }
        }

        await Task.WhenAll(tasks);
    }

    /// <inheritdoc />
    public async Task BroadcastDisconnectAsync()
    {
        var message = new { type = "disconnect" };
        var tasks = new List<Task>();

        foreach (var (id, socket) in _clients)
        {
            if (socket.State == WebSocketState.Open)
            {
                tasks.Add(SendAndCloseAsync(socket, message));
            }

            _clients.TryRemove(id, out _);
        }

        await Task.WhenAll(tasks);
    }

    private static async Task SendAsync(WebSocket socket, object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task SendAndCloseAsync(WebSocket socket, object message)
    {
        try
        {
            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None);
        }
        catch
        {
            // Best effort
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CliWebSocketClientInfo> GetClients()
    {
        return _clientInfo.Values.ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var (_, socket) in _clients)
        {
            socket.Dispose();
        }

        _clients.Clear();
    }
}
