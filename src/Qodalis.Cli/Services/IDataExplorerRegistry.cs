using Qodalis.Cli.Abstractions.DataExplorer;

namespace Qodalis.Cli.Services;

/// <summary>
/// Provides read-only access to registered data explorer providers and their metadata.
/// </summary>
public interface IDataExplorerRegistry
{
    /// <summary>
    /// Gets a registered provider and its options by source name.
    /// </summary>
    /// <param name="name">The data source name.</param>
    /// <returns>The provider and options tuple, or <c>null</c> if not found.</returns>
    (IDataExplorerProvider Provider, DataExplorerProviderOptions Options)? Get(string name);

    /// <summary>
    /// Returns metadata for all registered data sources.
    /// </summary>
    /// <returns>A list of data source information objects.</returns>
    List<DataExplorerSourceInfo> GetSources();

    /// <summary>
    /// Returns metadata for the data sources <paramref name="caller"/> may use (their <c>RequiredPermissions</c>),
    /// with <see cref="DataExplorerSourceInfo.CanExport"/> for that caller. Every source without a caller.
    /// </summary>
    /// <param name="caller">Who asks; <c>null</c> for trusted code.</param>
    /// <returns>A list of data source information objects.</returns>
    List<DataExplorerSourceInfo> GetSources(DataExplorerCaller? caller);
}
