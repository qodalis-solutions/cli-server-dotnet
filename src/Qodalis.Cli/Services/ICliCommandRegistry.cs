using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Services;

public interface ICliCommandRegistry
{
    IReadOnlyList<ICliCommandProcessor> Processors { get; }
    void Register(ICliCommandProcessor processor);
    ICliCommandProcessor? FindProcessor(string command, IEnumerable<string>? chainCommands = null);
}
