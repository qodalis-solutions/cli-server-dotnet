using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Tests.Helpers;

/// <summary>
/// A test processor that simulates a slow/long-running streaming command.
/// It respects the CancellationToken by calling ThrowIfCancellationRequested
/// before each chunk emission, and records whether it received a token.
/// </summary>
public class SlowStreamProcessor : CliCommandProcessor, ICliStreamCommandProcessor
{
    public override string Command { get; set; } = "slow-stream";
    public override string Description { get; set; } = "Simulates a slow streaming command for cancellation tests";

    /// <summary>
    /// Tracks how many chunks were emitted before cancellation (or completion).
    /// </summary>
    public int ChunksEmitted { get; private set; }

    /// <summary>
    /// Tracks the CancellationToken passed to HandleStreamAsync.
    /// </summary>
    public CancellationToken ReceivedCancellationToken { get; private set; }

    /// <summary>
    /// Delay between chunks in milliseconds. Default is 50 ms (fast enough for tests).
    /// </summary>
    public int ChunkDelayMs { get; set; } = 50;

    /// <summary>
    /// Total number of chunks to emit (if not cancelled). Default is 10.
    /// </summary>
    public int TotalChunks { get; set; } = 10;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        => Task.FromResult("non-streaming fallback");

    public async Task<int> HandleStreamAsync(
        CliProcessCommand command,
        Func<object, Task> emit,
        CancellationToken cancellationToken = default)
    {
        ReceivedCancellationToken = cancellationToken;
        ChunksEmitted = 0;

        for (var i = 0; i < TotalChunks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await emit(new { type = "text", value = $"chunk{i + 1}" });
            ChunksEmitted++;
            await Task.Delay(ChunkDelayMs, cancellationToken);
        }

        return 0;
    }
}
