using System.Net.WebSockets;

namespace Qodalis.Cli.Services;

/// <summary>
/// Manages WebSocket connections for server-push event broadcasting to connected clients.
/// </summary>
public interface ICliEventSocketManager : IDisposable
{
    /// <summary>
    /// Accepts and manages a WebSocket connection, keeping it alive until the client disconnects or the server shuts down.
    /// </summary>
    /// <param name="socket">The WebSocket connection.</param>
    /// <param name="cancellationToken">A token to signal server shutdown.</param>
    /// <param name="remoteAddress">The client's remote address for tracking.</param>
    Task HandleConnectionAsync(WebSocket socket, CancellationToken cancellationToken, string? remoteAddress = null);

    /// <summary>
    /// Broadcasts a text message to all connected WebSocket clients.
    /// </summary>
    /// <param name="message">The message to broadcast.</param>
    Task BroadcastMessageAsync(string message);

    /// <summary>
    /// Sends a disconnect message to all connected clients and closes their connections.
    /// </summary>
    Task BroadcastDisconnectAsync();

    /// <summary>
    /// Returns information about all currently connected event clients.
    /// </summary>
    IReadOnlyList<CliWebSocketClientInfo> GetClients();
}
