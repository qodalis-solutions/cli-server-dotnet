using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Security;

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

    /// <summary>Gets or sets the authenticated user id, when the server is protected.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? UserId { get; set; }
}

/// <summary>
/// Manages WebSocket connections for server-push event broadcasting to connected clients.
/// Sends to one socket are serialized (broadcasts and user-targeted messages may run concurrently).
/// Sockets authenticated with a session are bound to it (see <see cref="ICliSessionBoundSockets"/>).
/// Subscribes to every event of the <see cref="ICliEventBus"/> and sends each one to the sockets of its audience.
/// </summary>
public class CliEventSocketManager : ICliEventSocketManager
{
    private readonly ConcurrentDictionary<string, SerializedWebSocketSender> _clients = new();
    private readonly ConcurrentDictionary<string, CliWebSocketClientInfo> _clientInfo = new();
    private readonly ConcurrentDictionary<string, CliPrincipal> _bound = new();
    private readonly ConcurrentDictionary<string, CliPrincipal> _principals = new();
    private readonly ILogger<CliEventSocketManager> _logger;
    private readonly bool _requireAuthentication;
    private readonly IDisposable? _subscription;

    /// <summary>
    /// Initializes a new instance of <see cref="CliEventSocketManager"/>.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="eventBus">The bus whose events are sent to the sockets; none when null.</param>
    /// <param name="serverOptions">Server options: on a protected server, a socket without a principal receives only
    /// the events for everyone.</param>
    public CliEventSocketManager(ILogger<CliEventSocketManager> logger, ICliEventBus? eventBus = null,
        CliServerOptions? serverOptions = null)
    {
        _logger = logger;
        _requireAuthentication = serverOptions?.RequireAuthentication ?? false;
        _subscription = eventBus?.Subscribe("*", (e, _) => SendEventAsync(e));
    }

    /// <inheritdoc />
    public async Task HandleConnectionAsync(WebSocket socket, CancellationToken cancellationToken, string? remoteAddress = null,
        CliPrincipal? principal = null)
    {
        var id = Guid.NewGuid().ToString();
        var sender = new SerializedWebSocketSender(socket);
        _clients.TryAdd(id, sender);
        _clientInfo.TryAdd(id, new CliWebSocketClientInfo
        {
            Id = id,
            ConnectedAt = DateTime.UtcNow.ToString("o"),
            RemoteAddress = remoteAddress ?? "unknown",
            Type = "events",
            UserId = principal?.UserId,
        });
        if (principal != null) _principals.TryAdd(id, principal);
        if (SessionBoundSockets.IsBound(principal)) _bound.TryAdd(id, principal!);

        _logger.LogInformation("Event WebSocket client connected (id={ClientId})", id);

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
            _logger.LogDebug("Event WebSocket client disconnected unexpectedly (id={ClientId})", id);
        }
        finally
        {
            _bound.TryRemove(id, out _);
            _principals.TryRemove(id, out _);
            _clients.TryRemove(id, out _);
            _clientInfo.TryRemove(id, out _);
            sender.Dispose();
            _logger.LogInformation("Event WebSocket client disconnected (id={ClientId})", id);
        }
    }

    /// <inheritdoc />
    public async Task BroadcastMessageAsync(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var tasks = new List<Task>();

        foreach (var (_, sender) in _clients)
        {
            if (sender.Socket.State == WebSocketState.Open)
            {
                tasks.Add(sender.SendTextAsync(bytes));
            }
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Sends an event to the open sockets of its audience: every socket for everyone; otherwise the sockets whose
    /// principal has the permission or is the user. Without authentication (no principal) every socket receives it.
    /// A failed send to one socket does not stop the others.
    /// </summary>
    public async Task SendEventAsync(CliEvent cliEvent)
    {
        var bytes = CliEventBus.SerializeToUtf8Bytes(cliEvent);
        var tasks = new List<Task>();

        foreach (var (id, sender) in _clients)
        {
            if (sender.Socket.State != WebSocketState.Open) continue;
            if (!CanReceive(cliEvent.Audience, _principals.GetValueOrDefault(id))) continue;
            tasks.Add(SendQuietlyAsync(id, sender, bytes));
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>Whether a socket authenticated as <paramref name="principal"/> (null: none) receives an event for <paramref name="audience"/>.</summary>
    public bool CanReceive(CliEventAudience audience, CliPrincipal? principal)
    {
        if (audience.IsEveryone) return true;
        if (principal == null) return !_requireAuthentication;
        return (audience.Permission != null && principal.Can(audience.Permission)) ||
               (audience.UserId != null && audience.UserId == principal.UserId);
    }

    private async Task SendQuietlyAsync(string id, SerializedWebSocketSender sender, byte[] bytes)
    {
        try
        {
            await sender.SendTextAsync(bytes);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Sending an event to WebSocket client {ClientId} failed", id);
        }
    }

    /// <inheritdoc />
    public async Task SendToUserAsync(string userId, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var tasks = new List<Task>();

        foreach (var (id, sender) in _clients)
        {
            if (sender.Socket.State == WebSocketState.Open &&
                _clientInfo.TryGetValue(id, out var info) && info.UserId == userId)
            {
                tasks.Add(sender.SendTextAsync(bytes));
            }
        }

        await Task.WhenAll(tasks);
    }

    /// <inheritdoc />
    public async Task BroadcastDisconnectAsync()
    {
        _logger.LogInformation("Broadcasting disconnect to {Count} event clients", _clients.Count);
        var bytes = Serialize(new { type = "disconnect" });
        var tasks = new List<Task>();

        foreach (var (id, sender) in _clients)
        {
            if (sender.Socket.State == WebSocketState.Open)
            {
                tasks.Add(SendAndCloseAsync(sender, bytes));
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
            if (await SessionBoundSockets.IsActiveAsync(entry.Value, isSessionActive, _logger)) return;
            if (!_bound.TryRemove(entry.Key, out _)) return;

            // Stop delivering to it, then close after any send in progress.
            _clientInfo.TryRemove(entry.Key, out _);
            _principals.TryRemove(entry.Key, out _);
            if (_clients.TryRemove(entry.Key, out var sender))
            {
                _logger.LogInformation("Closing event WebSocket client of an ended session (id={ClientId})", entry.Key);
                await SessionBoundSockets.CloseAsync(sender, _logger);
            }
        }));
    }

    private static byte[] Serialize(object message) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

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
    public IReadOnlyList<CliWebSocketClientInfo> GetClients()
    {
        return _clientInfo.Values.ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _subscription?.Dispose();
        foreach (var (_, sender) in _clients)
        {
            sender.Socket.Dispose();
            sender.Dispose();
        }

        _clients.Clear();
    }
}
