namespace Qodalis.Cli.Abstractions.Jobs;

public interface ICliJobExecutionContext
{
    ICliJobLogger Logger { get; }
}
