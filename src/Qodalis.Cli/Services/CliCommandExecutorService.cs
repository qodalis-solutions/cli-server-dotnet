using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Models;

namespace Qodalis.Cli.Services;

public class CliCommandExecutorService : ICliCommandExecutorService
{
    private readonly ICliCommandRegistry _registry;
    private readonly ILogger<CliCommandExecutorService> _logger;

    public CliCommandExecutorService(ICliCommandRegistry registry, ILogger<CliCommandExecutorService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public async Task<CliServerResponse> ExecuteAsync(
        CliProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        var fullCommand = command.ChainCommands?.Any() == true
            ? $"{command.Command} {string.Join(" ", command.ChainCommands)}"
            : command.Command;

        _logger.LogInformation("Executing command: {Command}", fullCommand);

        var processor = _registry.FindProcessor(command.Command, command.ChainCommands);

        if (processor == null)
        {
            _logger.LogWarning("Unknown command: {Command}", fullCommand);
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

            _logger.LogInformation("Command completed: {Command}", fullCommand);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command failed: {Command}", fullCommand);
            responseBuilder.WriteText($"Error executing command: {ex.Message}", "error");
            responseBuilder.SetExitCode(1);
        }

        return responseBuilder.Build();
    }
}
