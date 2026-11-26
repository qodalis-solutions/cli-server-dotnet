using System.Net.WebSockets;

namespace Qodalis.Cli.Services;

/// <summary>
/// Manages WebSocket connections for server-push event broadcasting to connected clients.
/// </summary>
public interface ICliEventSocketManager : ICliSessionBoundSockets, IDisposable
{
    /// <summary>
    /// Accepts and manages a WebSocket connection, keeping it alive until the client disconnects or the server shuts down.
    /// </summary>
    /// <param name="socket">The WebSocket connection.</param>
    /// <param name="cancellationToken">A token to signal server shutdown.</param>
    /// <param name="remoteAddress">The client's remote address for tracking.</param>
    /// <param name="principal">The authenticated caller, when the server is protected; used for user-scoped events.
    /// A principal with a session id binds the socket to that session.</param>
    Task HandleConnectionAsync(WebSocket socket, CancellationToken cancellationToken, string? remoteAddress = null,
        Qodalis.Cli.Security.CliPrincipal? principal = null);

    /// <summary>
    /// Sends a text message only to event sockets authenticated as <paramref name="userId"/>
    /// (user-scoped events such as <c>session-kicked</c>).
    /// </summary>
    Task SendToUserAsync(string userId, string message);

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
    /// Sends a disconnect message to all connected clients and closes their connections with <paramref name="closeCode"/>
    /// (1000 on shutdown, 1012 on a restart).
    /// </summary>
    Task BroadcastDisconnectAsync(int closeCode, string reason) => BroadcastDisconnectAsync();

    /// <summary>
    /// Returns information about all currently connected event clients.
    /// </summary>
    IReadOnlyList<CliWebSocketClientInfo> GetClients();

    /// <summary>
    /// Rewrites every event of <paramref name="type"/> for each socket it is sent to (one hook per type; null removes
    /// it): the server's <c>notification:changed</c> carries each reader's own unread count. The hook gets the socket's
    /// principal on a protected server, else null; returning null skips that socket. A manager without hooks ignores it.
    /// </summary>
    void SetPersonalizer(string type, Func<Qodalis.Cli.Abstractions.Events.CliEvent, Qodalis.Cli.Security.CliPrincipal?, Qodalis.Cli.Abstractions.Events.CliEvent?>? personalizer)
    {
    }
}
