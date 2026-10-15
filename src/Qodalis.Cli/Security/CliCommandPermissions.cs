using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Security;

/// <summary>
/// Per-command permission rules shared by <c>/execute</c> and the MCP endpoint: when the server is
/// protected, the caller needs <c>commands:execute</c> (enforced by the route policy) plus every
/// <see cref="ICliCommandProcessor.RequiredPermissions"/> along the command chain.
/// </summary>
public static class CliCommandPermissions
{
    /// <summary>
    /// The permissions declared by every processor from the root <paramref name="command"/> through
    /// <paramref name="chainCommands"/> (stopping at the first unknown sub-command), de-duplicated.
    /// </summary>
    public static List<string> RequiredFor(ICliCommandRegistry registry, string command, IEnumerable<string>? chainCommands)
    {
        var required = new List<string>();
        var processor = registry.FindProcessor(command);
        if (processor?.RequiredPermissions != null) required.AddRange(processor.RequiredPermissions);
        foreach (var sub in chainCommands ?? [])
        {
            processor = processor?.Processors?.FirstOrDefault(
                p => string.Equals(p.Command, sub, StringComparison.OrdinalIgnoreCase));
            if (processor == null) break;
            if (processor.RequiredPermissions != null) required.AddRange(processor.RequiredPermissions);
        }
        return required.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The permissions of <paramref name="required"/> that <paramref name="principal"/> lacks.</summary>
    public static List<string> Missing(CliPrincipal principal, IEnumerable<string> required) =>
        CliPermissionMatcher.Missing(principal.Permissions, required);
}
