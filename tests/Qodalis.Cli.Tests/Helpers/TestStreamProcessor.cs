using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Tests.Helpers;

public class TestStreamProcessor : CliCommandProcessor, ICliStreamCommandProcessor
{
    public override string Command { get; set; } = "stream-test";
    public override string Description { get; set; } = "Test streaming processor";

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        => Task.FromResult("non-streaming fallback");

    public async Task<int> HandleStreamAsync(CliProcessCommand command, Func<object, Task> emit, CancellationToken cancellationToken = default)
    {
        await emit(new { type = "text", value = "chunk1" });
        await emit(new { type = "text", value = "chunk2" });
        await emit(new { type = "text", value = "chunk3" });
        return 0;
    }
}
