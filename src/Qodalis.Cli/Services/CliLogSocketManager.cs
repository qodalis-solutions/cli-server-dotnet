using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Qodalis.Cli.Services;

/// <summary>
/// Manages WebSocket connections for real-time log streaming, supporting per-client level filtering.
/// </summary>
public class CliLogSocketManager : ICliLogSocketManager
{
    private static readonly string[] LogLevelOrder = { "verbose", "debug", "information", "warning", "error", "fatal" };

    private readonly ConcurrentDictionary<string, (WebSocket Socket, string? LevelFilter)> _clients = new();
    private readonly ILogger<CliLogSocketManager> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CliLogSocketManager"/>.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public CliLogSocketManager(ILogger<CliLogSocketManager> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task HandleConnectionAsync(WebSocket socket, string? levelFilter, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString();
        _clients.TryAdd(id, (socket, levelFilter));

        _logger.LogInformation("Log WebSocket client connected (id={ClientId}, level={LevelFilter})", id, levelFilter ?? "all");

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
            _logger.LogInformation("Log WebSocket client disconnected (id={ClientId})", id);
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
            if (client.Socket.State == WebSocketState.Open && ShouldSendLog(client.LevelFilter, level))
            {
                tasks.Add(SendBytesAsync(client.Socket, bytes));
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
        _logger.LogInformation("Broadcasting disconnect to {Count} log clients", _clients.Count);
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

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var (_, client) in _clients)
        {
            client.Socket.Dispose();
        }

        _clients.Clear();
    }
}
