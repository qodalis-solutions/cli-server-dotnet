using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Services;

/// <summary>
/// Default implementation of <see cref="ICliCommandRegistry"/> that stores processors in memory
/// and supports lookup with sub-command traversal.
/// </summary>
public class CliCommandRegistry : ICliCommandRegistry
{
    private readonly List<ICliCommandProcessor> _processors = [];
    private readonly ILogger<CliCommandRegistry> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CliCommandRegistry"/>.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public CliCommandRegistry(ILogger<CliCommandRegistry> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<ICliCommandProcessor> Processors => _processors;

    /// <inheritdoc />
    public void Register(ICliCommandProcessor processor)
    {
        var existingIndex = _processors.FindIndex(
            p => string.Equals(p.Command, processor.Command, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
            _processors[existingIndex] = processor;
        else
            _processors.Add(processor);

        _logger.LogDebug("Registered command processor: {Command}", processor.Command);
    }

    /// <inheritdoc />
    public ICliCommandProcessor? FindProcessor(string command, IEnumerable<string>? chainCommands = null)
    {
        // A command name wins over another command's alias
        var processor = CliCommandNames.Find(_processors, command);

        if (processor == null || chainCommands == null)
            return processor;

        foreach (var sub in chainCommands)
        {
            var child = CliCommandNames.Find(processor.Processors, sub);

            if (child == null) break;
            processor = child;
        }

        return processor;
    }
}
