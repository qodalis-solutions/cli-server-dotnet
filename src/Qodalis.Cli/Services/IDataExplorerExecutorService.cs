using Qodalis.Cli.Abstractions.DataExplorer;

namespace Qodalis.Cli.Services;

/// <summary>
/// Defines a service for executing data explorer queries against registered providers.
/// </summary>
public interface IDataExplorerExecutorService
{
    /// <summary>
    /// Executes a data explorer query against the specified source.
    /// </summary>
    /// <param name="request">The query execution request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The query result with execution metadata.</returns>
    Task<DataExplorerResult> ExecuteAsync(
        DataExplorerExecuteRequest request,
        CancellationToken cancellationToken = default);
}
