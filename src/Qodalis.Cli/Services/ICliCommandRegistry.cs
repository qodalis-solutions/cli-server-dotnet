using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Services;

/// <summary>
/// Registry for CLI command processors, providing registration and lookup by command name.
/// </summary>
public interface ICliCommandRegistry
{
    /// <summary>
    /// Gets all registered command processors.
    /// </summary>
    IReadOnlyList<ICliCommandProcessor> Processors { get; }

    /// <summary>
    /// Registers a command processor. If a processor with the same command already exists, it is replaced.
    /// </summary>
    /// <param name="processor">The processor to register.</param>
    void Register(ICliCommandProcessor processor);

    /// <summary>
    /// Finds the most specific processor matching the command and optional chain of sub-commands.
    /// </summary>
    /// <param name="command">The primary command keyword.</param>
    /// <param name="chainCommands">Optional chain of sub-command keywords to traverse.</param>
    /// <returns>The matching processor, or <c>null</c> if not found.</returns>
    ICliCommandProcessor? FindProcessor(string command, IEnumerable<string>? chainCommands = null);
}
