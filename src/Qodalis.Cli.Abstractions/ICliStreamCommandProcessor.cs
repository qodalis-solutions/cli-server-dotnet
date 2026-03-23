namespace Qodalis.Cli.Abstractions;

/// <summary>
/// Optional interface for command processors that support streaming output.
/// Processors implementing this interface can emit output chunks incrementally,
/// enabling real-time rendering on the client via Server-Sent Events.
/// </summary>
public interface ICliStreamCommandProcessor
{
    /// <summary>
    /// Execute the command, calling <paramref name="emit"/> for each output chunk.
    /// The emit callback receives output objects that will be serialized as SSE events.
    /// </summary>
    /// <param name="command">Parsed command with arguments.</param>
    /// <param name="emit">Async callback to send a single output chunk to the client.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Exit code (0 for success).</returns>
    Task<int> HandleStreamAsync(CliProcessCommand command, Func<object, Task> emit, CancellationToken cancellationToken = default);
}
