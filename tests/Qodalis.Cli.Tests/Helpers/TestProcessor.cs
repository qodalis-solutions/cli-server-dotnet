using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Tests.Helpers;

public class TestProcessor : CliCommandProcessor
{
    public override string Command { get; set; }
    public override string Description { get; set; }

    private readonly Func<CliProcessCommand, CancellationToken, Task<string>> _handler;

    public TestProcessor(string command, string description, Func<CliProcessCommand, CancellationToken, Task<string>>? handler = null, int apiVersion = 1)
    {
        Command = command;
        Description = description;
        ApiVersion = apiVersion;
        _handler = handler ?? ((_,_) => Task.FromResult($"Executed {command}"));
    }

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        => _handler(command, cancellationToken);
}
