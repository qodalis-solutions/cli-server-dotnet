using System.Net.WebSockets;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Services;

/// <summary>
/// Manages interactive shell sessions over WebSocket, spawning a PTY process and bridging I/O.
/// Access control happens before (see <see cref="CliShellAccess"/>); the manager enforces the
/// limits, timeouts, session binding and environment of <see cref="CliShellOptions"/>.
/// </summary>
public interface IShellSessionManager
{
    /// <summary>
    /// Handles an accepted shell WebSocket: reserves a slot (4429 when a limit is reached),
    /// spawns the process and bridges stdin/stdout until the process exits, the client closes,
    /// a timeout fires (4408) or the bound session ends (4401).
    /// </summary>
    Task HandleShellSessionAsync(WebSocket webSocket, ShellSessionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Handles an interactive shell session over WebSocket without an authenticated owner.
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
        CancellationToken cancellationToken) =>
        HandleShellSessionAsync(webSocket, new ShellSessionRequest { Cols = cols, Rows = rows, Command = command },
            cancellationToken);
}

/// <summary>Parameters of a shell session.</summary>
public sealed class ShellSessionRequest
{
    /// <summary>Terminal width (default 80).</summary>
    public int Cols { get; init; } = 80;

    /// <summary>Terminal height (default 24).</summary>
    public int Rows { get; init; } = 24;

    /// <summary>One-shot command (<c>command</c> mode); null for an interactive shell. Never logged or audited.</summary>
    public string? Command { get; init; }

    /// <summary>The access mode that admitted the connection (<see cref="CliShellAuthModes"/>).</summary>
    public string AuthMode { get; init; } = CliShellAuthModes.Anonymous;

    /// <summary>The authenticated owner (users mode); the shell is bound to its session.</summary>
    public CliPrincipal? Principal { get; init; }

    /// <summary>Re-validates <see cref="Principal"/> and records audit entries (users mode).</summary>
    public ICliAuthBridge? AuthBridge { get; init; }

    /// <summary>Client address (limit key outside users mode, logs and audit).</summary>
    public string? RemoteAddress { get; init; }
}
