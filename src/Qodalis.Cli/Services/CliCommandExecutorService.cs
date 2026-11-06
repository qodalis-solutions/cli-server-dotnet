using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Models;
using Qodalis.Cli.Security;

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
    private readonly int _maxTableRows;
    private readonly CliCommandUsage? _usage;
    private readonly ICliEventBus? _events;
    private readonly IHttpContextAccessor? _http;

    /// <summary>
    /// Initializes a new instance of <see cref="CliCommandExecutorService"/>.
    /// </summary>
    /// <param name="registry">The command processor registry.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="filters">Optional processor filters for runtime enable/disable checks.</param>
    /// <param name="serverOptions">Server options; <see cref="CliServerOptions.MaxTableRows"/> caps unpaged tables.</param>
    /// <param name="usage">Counts the executions (<c>GET /api/v1/qcli/commands/stats</c>); null to not count.</param>
    /// <param name="eventBus">Publishes <c>command:executed</c>; null to not publish.</param>
    /// <param name="http">Gives the caller of the current request (the event's <c>userId</c>).</param>
    public CliCommandExecutorService(
        ICliCommandRegistry registry,
        ILogger<CliCommandExecutorService> logger,
        IEnumerable<ICliProcessorFilter> filters,
        CliServerOptions? serverOptions = null,
        CliCommandUsage? usage = null,
        ICliEventBus? eventBus = null,
        IHttpContextAccessor? http = null)
    {
        _usage = usage;
        _events = eventBus;
        _http = http;
        _registry = registry;
        _logger = logger;
        _filters = filters;
        _maxTableRows = (serverOptions ?? new CliServerOptions()).MaxTableRows;
    }

    /// <inheritdoc />
    public bool IsBlocked(ICliCommandProcessor processor)
    {
        return _filters.Any(f => !f.IsAllowed(processor));
    }

    /// <inheritdoc />
    public async Task<CliServerResponse> ExecuteAsync(
        CliProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var known = false;
        var failed = true;
        try
        {
            var response = await RunAsync(command, found => known = found, cancellationToken);
            failed = response.ExitCode != 0;
            return response;
        }
        finally
        {
            var duration = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            _usage?.Record(command.Command, known, duration, failed);
            await CliCommandEvents.PublishAsync(_events, command.Command, known, !failed, duration,
                _http?.HttpContext?.GetCliPrincipal());
        }
    }

    private async Task<CliServerResponse> RunAsync(
        CliProcessCommand command,
        Action<bool> resolved,
        CancellationToken cancellationToken)
    {
        var fullCommand = command.ChainCommands?.Any() == true
            ? $"{command.Command} {string.Join(" ", command.ChainCommands)}"
            : command.Command;

        _logger.LogInformation("Executing command: {Command}", fullCommand);

        var processor = _registry.FindProcessor(command.Command, command.ChainCommands);
        resolved(processor != null);

        if (processor == null)
        {
            _logger.LogWarning("Unknown command: {Command}", fullCommand);
            var builder = new CliResponseBuilder();
            builder.WriteText($"Unknown command: {command.Command}", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        if (IsBlocked(processor))
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
                return CliOutputLimits.Apply(structuredResponse, _maxTableRows);
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
