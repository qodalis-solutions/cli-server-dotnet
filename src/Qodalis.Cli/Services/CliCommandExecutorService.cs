using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Models;

namespace Qodalis.Cli.Services;

public class CliCommandExecutorService : ICliCommandExecutorService
{
    private readonly ICliCommandRegistry _registry;

    public CliCommandExecutorService(ICliCommandRegistry registry)
    {
        _registry = registry;
    }

    public async Task<CliServerResponse> ExecuteAsync(
        CliProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        var processor = _registry.FindProcessor(command.Command, command.ChainCommands);

        if (processor == null)
        {
            var builder = new CliResponseBuilder();
            builder.WriteText($"Unknown command: {command.Command}", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var responseBuilder = new CliResponseBuilder();

        try
        {
            var result = await processor.HandleAsync(command, cancellationToken);

            if (!string.IsNullOrEmpty(result))
            {
                responseBuilder.WriteText(result);
            }
        }
        catch (Exception ex)
        {
            responseBuilder.WriteText($"Error executing command: {ex.Message}", "error");
            responseBuilder.SetExitCode(1);
        }

        return responseBuilder.Build();
    }
}
