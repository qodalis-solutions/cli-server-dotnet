namespace Qodalis.Cli.Abstractions.DataExplorer;

public interface IDataExplorerProvider
{
    Task<DataExplorerResult> ExecuteAsync(
        DataExplorerExecutionContext context,
        CancellationToken cancellationToken = default);

    Task<DataExplorerSchemaResult?> GetSchemaAsync(
        DataExplorerProviderOptions options,
        CancellationToken cancellationToken = default)
        => Task.FromResult<DataExplorerSchemaResult?>(null);
}
