using System.Net.WebSockets;

namespace Qodalis.Cli.Services;

/// <summary>
/// Manages WebSocket connections for real-time log streaming, supporting per-client level filtering.
/// </summary>
public interface ICliLogSocketManager : IDisposable
{
    /// <summary>
    /// Accepts a WebSocket connection for log streaming with an optional minimum log level filter.
    /// </summary>
    /// <param name="socket">The WebSocket connection.</param>
    /// <param name="levelFilter">Optional minimum log level (e.g., "warning" to receive warning, error, and fatal only).</param>
    /// <param name="cancellationToken">A token to signal server shutdown.</param>
    Task HandleConnectionAsync(WebSocket socket, string? levelFilter, CancellationToken cancellationToken);

    /// <summary>
    /// Broadcasts a log message to all connected clients whose level filter permits it.
    /// </summary>
    /// <param name="level">The log level (e.g., "information", "error").</param>
    /// <param name="message">The log message text.</param>
    /// <param name="category">The logger category name.</param>
    Task BroadcastLogAsync(string level, string message, string category);

    /// <summary>
    /// Sends a disconnect message to all connected log clients and closes their connections.
    /// </summary>
    Task BroadcastDisconnectAsync();
}
