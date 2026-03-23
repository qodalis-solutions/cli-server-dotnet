using System.Net.WebSockets;

namespace Qodalis.Cli.Services;

/// <summary>
/// Manages interactive shell sessions over WebSocket, spawning a PTY process and bridging I/O.
/// </summary>
public interface IShellSessionManager
{
    /// <summary>
    /// Handles an interactive shell session over WebSocket, spawning a process and bridging stdin/stdout.
    /// </summary>
    /// <param name="webSocket">The WebSocket connection for the session.</param>
    /// <param name="cols">The terminal width in columns.</param>
    /// <param name="rows">The terminal height in rows.</param>
    /// <param name="command">Optional command to execute instead of an interactive shell.</param>
    /// <param name="cancellationToken">A token to cancel the session.</param>
    Task HandleShellSessionAsync(
        WebSocket webSocket,
        int cols,
        int rows,
        string? command,
        CancellationToken cancellationToken);
}
