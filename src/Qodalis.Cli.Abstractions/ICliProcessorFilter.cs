namespace Qodalis.Cli.Abstractions;

/// <summary>
/// Provides a mechanism to filter whether a command processor is allowed to execute.
/// Implementations can use this to disable processors at runtime (e.g., when a plugin is toggled off).
/// </summary>
public interface ICliProcessorFilter
{
    /// <summary>
    /// Determines whether the given command processor is allowed to execute.
    /// </summary>
    /// <param name="processor">The command processor to check.</param>
    /// <returns><c>true</c> if the processor is allowed; <c>false</c> if it should be blocked.</returns>
    bool IsAllowed(ICliCommandProcessor processor);
}
