using Microsoft.Extensions.Logging;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Services;

/// <summary>
/// Server-push sockets (events, logs) bound to the authentication session that opened them. When
/// that session ends (logout, revoke, user disabled/deleted, expiry) the socket is closed with
/// 4401 <c>session ended</c>. Sockets opened with an API token are bound to that token the same way.
/// Sockets without a session or token id are never checked.
/// </summary>
public interface ICliSessionBoundSockets
{
    /// <summary>
    /// Re-checks every socket bound to a session and closes (4401 <c>session ended</c>) those whose
    /// session <paramref name="isSessionActive"/> reports as ended; a failing check also closes the
    /// socket. Called by authentication plugins whenever sessions, users or groups change.
    /// </summary>
    Task CloseEndedSessionsAsync(Func<CliPrincipal, Task<bool>> isSessionActive);
}

/// <summary>Helpers shared by the session-bound socket managers.</summary>
internal static class SessionBoundSockets
{
    public const string SessionEndedReason = "session ended";

    /// <summary>Only principals carrying a session or API token id are bound to it.</summary>
    public static bool IsBound(CliPrincipal? principal) => principal?.SessionId != null || principal?.TokenId != null;

    /// <summary>Runs the check; an exception counts as an ended session.</summary>
    public static async Task<bool> IsActiveAsync(CliPrincipal principal, Func<CliPrincipal, Task<bool>> isSessionActive,
        ILogger? logger)
    {
        try
        {
            return await isSessionActive(principal);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Socket session check failed; closing the socket");
            return false;
        }
    }

    /// <summary>Closes with 4401 after any send in progress; best effort.</summary>
    public static async Task CloseAsync(SerializedWebSocketSender sender, ILogger? logger)
    {
        try
        {
            await sender.CloseAsync(CliShellCloseCodes.Unauthorized, SessionEndedReason);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed to close the socket of an ended session");
        }
    }
}
