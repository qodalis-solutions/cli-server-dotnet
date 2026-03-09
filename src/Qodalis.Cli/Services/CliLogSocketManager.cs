using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Qodalis.Cli.Services;

public class CliLogSocketManager : IDisposable
{
    private static readonly string[] LogLevelOrder = { "verbose", "debug", "information", "warning", "error", "fatal" };

    private readonly ConcurrentDictionary<string, (WebSocket Socket, string? LevelFilter)> _clients = new();

    public async Task HandleConnectionAsync(WebSocket socket, string? levelFilter, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString();
        _clients.TryAdd(id, (socket, levelFilter));

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
        }
    }

    public async Task BroadcastLogAsync(string level, string message, string category)
    {
        var json = FormatLogMessage(level, message, category);
        var bytes = Encoding.UTF8.GetBytes(json);
        var tasks = new List<Task>();

        foreach (var (id, client) in _clients)
        {
            if (client.Socket.State == WebSocketState.Open && ShouldSendLog(client.LevelFilter, level))
            {
                tasks.Add(SendBytesAsync(client.Socket, bytes));
            }
        }

        await Task.WhenAll(tasks);
    }

    public static bool ShouldSendLog(string? filterLevel, string logLevel)
    {
        if (string.IsNullOrEmpty(filterLevel))
        {
            return true;
        }

        var filterIndex = Array.FindIndex(LogLevelOrder, l => l.Equals(filterLevel, StringComparison.OrdinalIgnoreCase));
        var logIndex = Array.FindIndex(LogLevelOrder, l => l.Equals(logLevel, StringComparison.OrdinalIgnoreCase));

        if (filterIndex < 0 || logIndex < 0)
        {
            return true;
        }

        return logIndex >= filterIndex;
    }

    public static string FormatLogMessage(string level, string message, string category)
    {
        var payload = new
        {
            type = "log",
            timestamp = DateTime.UtcNow.ToString("o"),
            level,
            message,
            category
        };

        return JsonSerializer.Serialize(payload);
    }

    public async Task BroadcastDisconnectAsync()
    {
        var message = new { type = "disconnect" };
        var tasks = new List<Task>();

        foreach (var (id, client) in _clients)
        {
            if (client.Socket.State == WebSocketState.Open)
            {
                tasks.Add(SendAndCloseAsync(client.Socket, message));
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

    private static async Task SendBytesAsync(WebSocket socket, byte[] bytes)
    {
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch
        {
            // Best effort
        }
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

    public void Dispose()
    {
        foreach (var (_, client) in _clients)
        {
            client.Socket.Dispose();
        }

        _clients.Clear();
    }
}
