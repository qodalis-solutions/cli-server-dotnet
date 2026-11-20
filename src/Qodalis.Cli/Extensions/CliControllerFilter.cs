using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Qodalis.Cli.Extensions;

/// <summary>
/// With the CLI routes turned off (<see cref="CliBuilder.WithoutCliRoutes"/>), removes the CLI's controllers from
/// whatever MVC discovered. A Web SDK host lists every referenced assembly that uses MVC as an application part
/// (<c>[assembly: ApplicationPart("Qodalis.Cli")]</c>, generated at build time), so core's and the plugins' controllers
/// would be routable even though <c>AddCli</c> did not add them. Removed: every controller of <c>Qodalis.Cli</c>, of
/// an assembly named <c>Qodalis.Cli.Plugin.*</c> and of an assembly a plugin registered with
/// <see cref="CliBuilder.AddApplicationPart"/>. The host's own controllers stay. Runs after MVC's own provider.
/// </summary>
internal sealed class CliControllerFilter : IApplicationFeatureProvider<ControllerFeature>
{
    private const string PluginPrefix = "Qodalis.Cli.Plugin.";
    private readonly HashSet<Assembly> _cliAssemblies;

    public CliControllerFilter(IEnumerable<Assembly> pluginParts)
    {
        _cliAssemblies = [typeof(CliControllerFilter).Assembly, .. pluginParts];
    }

    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        foreach (var controller in feature.Controllers.Where(IsCliController).ToList())
            feature.Controllers.Remove(controller);
    }

    private bool IsCliController(TypeInfo controller)
    {
        var assembly = controller.Assembly;
        return _cliAssemblies.Contains(assembly) ||
               assembly.GetName().Name?.StartsWith(PluginPrefix, StringComparison.Ordinal) == true;
    }
}
