using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Services;

/// <summary>
/// The module that registered each command processor (<see cref="Extensions.CliBuilder.AddModule"/>).
/// <c>GET /api/v1/qcli/commands</c> lists it as the command's <c>module</c> when the processor names none itself.
/// </summary>
public sealed class CliProcessorModules
{
    private readonly Dictionary<ICliCommandProcessor, string> _modules = new(ReferenceEqualityComparer.Instance);

    internal void Add(ICliCommandProcessor processor, string module)
    {
        if (!string.IsNullOrWhiteSpace(module)) _modules[processor] = module;
    }

    /// <summary>The name of the module that registered <paramref name="processor"/>, or <c>null</c>.</summary>
    public string? Of(ICliCommandProcessor processor) => _modules.GetValueOrDefault(processor);
}
