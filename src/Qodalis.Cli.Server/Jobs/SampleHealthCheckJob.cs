using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Server.Jobs;

public class SampleHealthCheckJob : ICliJob
{
    public async Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken)
    {
        context.Logger.Info("Running health check...");
        await Task.Delay(500, cancellationToken);

        var rng = Random.Shared.Next(100);
        if (rng < 90)
        {
            context.Logger.Info($"Health check passed (score: {rng})");
        }
        else
        {
            context.Logger.Warning($"Health check degraded (score: {rng})");
        }
    }
}
