namespace Qodalis.Cli.Abstractions.DataExplorer;

/// <summary>
/// Defines a provider that can execute queries and optionally introspect the schema of a data source.
/// </summary>
public interface IDataExplorerProvider
{
    /// <summary>
    /// Executes a query against the data source.
    /// </summary>
    /// <param name="context">The execution context containing the query, parameters, and options.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The query result including rows, columns, and execution metadata.</returns>
    Task<DataExplorerResult> ExecuteAsync(
        DataExplorerExecutionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the schema of the data source, if supported. Returns <c>null</c> when schema introspection
    /// is not available.
    /// </summary>
    /// <param name="options">The provider options identifying the data source.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The schema result, or <c>null</c> if not supported.</returns>
    Task<DataExplorerSchemaResult?> GetSchemaAsync(
        DataExplorerProviderOptions options,
        CancellationToken cancellationToken = default)
        => Task.FromResult<DataExplorerSchemaResult?>(null);
}
