using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Demo.Jobs;

public class SampleHealthCheckJob : ICliJob
{
    public async Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken)
    {
        context.Logger.Info("Running health check...");

        await Task.Delay(500, cancellationToken);

        context.Logger.Info("Health check passed");
    }
}
