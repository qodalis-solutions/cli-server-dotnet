using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Models;

namespace Qodalis.Cli.Services;

/// <summary>
/// Default implementation of <see cref="ICliCommandExecutorService"/> that resolves processors from
/// the registry and executes commands, preferring structured responses when available.
/// </summary>
public class CliCommandExecutorService : ICliCommandExecutorService
{
    private readonly ICliCommandRegistry _registry;
    private readonly ILogger<CliCommandExecutorService> _logger;
    private readonly IEnumerable<ICliProcessorFilter> _filters;

    /// <summary>
    /// Initializes a new instance of <see cref="CliCommandExecutorService"/>.
    /// </summary>
    /// <param name="registry">The command processor registry.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="filters">Optional processor filters for runtime enable/disable checks.</param>
    public CliCommandExecutorService(
        ICliCommandRegistry registry,
        ILogger<CliCommandExecutorService> logger,
        IEnumerable<ICliProcessorFilter> filters)
    {
        _registry = registry;
        _logger = logger;
        _filters = filters;
    }

    /// <inheritdoc />
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

        if (_filters.Any(f => !f.IsAllowed(processor)))
        {
            _logger.LogWarning("Command blocked by filter (plugin disabled): {Command}", fullCommand);
            var builder = new CliResponseBuilder();
            builder.WriteText($"Command '{command.Command}' is currently disabled.", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var responseBuilder = new CliResponseBuilder();

        try
        {
            var structured = await processor.HandleStructuredAsync(command, cancellationToken);
            if (structured is CliServerResponse structuredResponse)
            {
                _logger.LogInformation("Command completed (structured): {Command}", fullCommand);
                return structuredResponse;
            }

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
