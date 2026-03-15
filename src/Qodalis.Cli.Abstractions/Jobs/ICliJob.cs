namespace Qodalis.Cli.Abstractions.Jobs;

public interface ICliJob
{
    Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken);
}
