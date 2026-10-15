using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Services;

/// <summary>
/// Manages WebSocket connections for real-time log streaming, supporting per-client level filtering.
/// Sends to one socket are serialized (logs are broadcast from many threads). Sockets authenticated
/// with a session are bound to it (see <see cref="ICliSessionBoundSockets"/>).
/// Note: This class cannot use ILogger because it is part of the logging pipeline itself
/// (WebSocketLoggerProvider depends on it), which would create a circular dependency.
/// </summary>
public class CliLogSocketManager : ICliLogSocketManager
{
    private static readonly string[] LogLevelOrder = { "verbose", "debug", "information", "warning", "error", "fatal" };

    private readonly ConcurrentDictionary<string, (SerializedWebSocketSender Sender, string? LevelFilter)> _clients = new();
    private readonly ConcurrentDictionary<string, CliPrincipal> _bound = new();

    /// <inheritdoc />
    public async Task HandleConnectionAsync(WebSocket socket, string? levelFilter, CancellationToken cancellationToken,
        CliPrincipal? principal = null)
    {
        var id = Guid.NewGuid().ToString();
        var sender = new SerializedWebSocketSender(socket);
        _clients.TryAdd(id, (sender, levelFilter));
        if (SessionBoundSockets.IsBound(principal)) _bound.TryAdd(id, principal!);

        try
        {
            // Send connected event
            await sender.SendTextAsync(Serialize(new { type = "connected" }), cancellationToken);

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
            _bound.TryRemove(id, out _);
            _clients.TryRemove(id, out _);
            sender.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task BroadcastLogAsync(string level, string message, string category)
    {
        var json = FormatLogMessage(level, message, category);
        var bytes = Encoding.UTF8.GetBytes(json);
        var tasks = new List<Task>();

        foreach (var (id, client) in _clients)
        {
            if (client.Sender.Socket.State == WebSocketState.Open && ShouldSendLog(client.LevelFilter, level))
            {
                tasks.Add(SendBytesAsync(client.Sender, bytes));
            }
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Determines whether a log message should be sent based on the client's level filter.
    /// </summary>
    /// <param name="filterLevel">The client's minimum log level filter, or <c>null</c> for no filtering.</param>
    /// <param name="logLevel">The log level of the message.</param>
    /// <returns><c>true</c> if the message should be sent; otherwise <c>false</c>.</returns>
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

    /// <summary>
    /// Formats a log message as a JSON string for WebSocket transmission.
    /// </summary>
    /// <param name="level">The log level.</param>
    /// <param name="message">The log message.</param>
    /// <param name="category">The logger category.</param>
    /// <returns>A JSON-serialized log message string.</returns>
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

    /// <inheritdoc />
    public async Task BroadcastDisconnectAsync()
    {
        var bytes = Serialize(new { type = "disconnect" });
        var tasks = new List<Task>();

        foreach (var (id, client) in _clients)
        {
            if (client.Sender.Socket.State == WebSocketState.Open)
            {
                tasks.Add(SendAndCloseAsync(client.Sender, bytes));
            }

            _clients.TryRemove(id, out _);
        }

        await Task.WhenAll(tasks);
    }

    /// <inheritdoc />
    public async Task CloseEndedSessionsAsync(Func<CliPrincipal, Task<bool>> isSessionActive)
    {
        await Task.WhenAll(_bound.ToArray().Select(async entry =>
        {
            if (await SessionBoundSockets.IsActiveAsync(entry.Value, isSessionActive, null)) return;
            if (!_bound.TryRemove(entry.Key, out _)) return;

            // Stop delivering to it, then close after any send in progress.
            if (_clients.TryRemove(entry.Key, out var client))
                await SessionBoundSockets.CloseAsync(client.Sender, null);
        }));
    }

    private static byte[] Serialize(object message) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

    private static async Task SendBytesAsync(SerializedWebSocketSender sender, byte[] bytes)
    {
        try
        {
            await sender.SendTextAsync(bytes);
        }
        catch
        {
            // Best effort
        }
    }

    private static async Task SendAndCloseAsync(SerializedWebSocketSender sender, byte[] bytes)
    {
        try
        {
            await sender.SendAndCloseAsync(bytes, "Server shutting down");
        }
        catch
        {
            // Best effort
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var (_, client) in _clients)
        {
            client.Sender.Socket.Dispose();
            client.Sender.Dispose();
        }

        _clients.Clear();
    }
}
