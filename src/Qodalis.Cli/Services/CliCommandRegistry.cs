using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Services;

public class CliCommandRegistry : ICliCommandRegistry
{
    private readonly List<ICliCommandProcessor> _processors = [];

    public IReadOnlyList<ICliCommandProcessor> Processors => _processors;

    public void Register(ICliCommandProcessor processor)
    {
        var existingIndex = _processors.FindIndex(
            p => string.Equals(p.Command, processor.Command, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
            _processors[existingIndex] = processor;
        else
            _processors.Add(processor);
    }

    public ICliCommandProcessor? FindProcessor(string command, IEnumerable<string>? chainCommands = null)
    {
        var processor = _processors.FirstOrDefault(
            p => string.Equals(p.Command, command, StringComparison.OrdinalIgnoreCase));

        if (processor == null || chainCommands == null)
            return processor;

        foreach (var sub in chainCommands)
        {
            var child = processor.Processors?.FirstOrDefault(
                p => string.Equals(p.Command, sub, StringComparison.OrdinalIgnoreCase));

            if (child == null) break;
            processor = child;
        }

        return processor;
    }
}
