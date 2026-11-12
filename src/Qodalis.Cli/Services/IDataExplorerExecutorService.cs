using Qodalis.Cli.Abstractions.DataExplorer;

namespace Qodalis.Cli.Services;

/// <summary>
/// Defines a service for executing data explorer queries against registered providers.
/// </summary>
public interface IDataExplorerExecutorService
{
    /// <summary>
    /// Executes a data explorer query against the specified source as trusted code (no caller: the source's
    /// permissions and rate limit do not apply; its read-only rules, allowed objects and masking do).
    /// </summary>
    /// <param name="request">The query execution request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The query result with execution metadata.</returns>
    Task<DataExplorerResult> ExecuteAsync(
        DataExplorerExecuteRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a data explorer query against the specified source as <paramref name="caller"/>: the source's
    /// permissions, rate limit, read-only rules and allowed objects first, then the provider, the row limit, column
    /// masking and the audit entry. Never throws for a refused or failed query: the result carries the error (and
    /// <see cref="DataExplorerResult.ErrorCode"/> for refusals).
    /// </summary>
    /// <param name="request">The query execution request.</param>
    /// <param name="caller">Who runs it; <c>null</c> for trusted code.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The query result with execution metadata.</returns>
    Task<DataExplorerResult> ExecuteAsync(
        DataExplorerExecuteRequest request,
        DataExplorerCaller? caller,
        CancellationToken cancellationToken = default);
}
