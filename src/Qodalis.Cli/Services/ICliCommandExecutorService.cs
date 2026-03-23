using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Models;

namespace Qodalis.Cli.Services;

/// <summary>
/// Service for executing CLI commands by resolving the appropriate processor and returning a structured response.
/// </summary>
public interface ICliCommandExecutorService
{
    /// <summary>
    /// Executes a CLI command and returns the structured response.
    /// </summary>
    /// <param name="command">The parsed command to execute.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The structured server response containing output blocks and an exit code.</returns>
    Task<CliServerResponse> ExecuteAsync(CliProcessCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true if the given processor is blocked by any registered filter.
    /// </summary>
    /// <param name="processor">The processor to check.</param>
    /// <returns><c>true</c> if any filter disallows the processor; otherwise <c>false</c>.</returns>
    bool IsBlocked(ICliCommandProcessor processor);
}
