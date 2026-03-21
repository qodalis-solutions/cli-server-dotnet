namespace Qodalis.Cli.Abstractions.DataExplorer;

public interface IDataExplorerProvider
{
    Task<DataExplorerResult> ExecuteAsync(
        DataExplorerExecutionContext context,
        CancellationToken cancellationToken = default);
}
