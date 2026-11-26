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
            processor = CliCommandNames.Find(processor?.Processors, sub);
            if (processor == null) break;
            if (processor.RequiredPermissions != null) required.AddRange(processor.RequiredPermissions);
        }
        return required.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Permissions a caller who is not signed in never gets on a server without authentication, unless the host sets
    /// <see cref="CliServerOptions.AllowAnonymousAdmin"/>: installing server and dashboard plugins and changing settings
    /// (each can run code on the server). Reading stays open.
    /// </summary>
    public static readonly IReadOnlyList<string> SignInRequired =
    [
        CliRoutePolicy.PluginsInstall,
        CliRoutePolicy.DashboardInstall,
        Qodalis.Cli.Abstractions.Config.CliConfigPermissions.Write,
    ];

    /// <summary>
    /// The permissions of <paramref name="required"/> an anonymous caller is refused on a server without
    /// authentication (<see cref="SignInRequired"/>); none when <see cref="CliServerOptions.AllowAnonymousAdmin"/> is set.
    /// </summary>
    public static List<string> RefusedAnonymously(CliServerOptions options, IEnumerable<string> required) =>
        options.AllowAnonymousAdmin
            ? []
            : required.Where(r => SignInRequired.Any(s => string.Equals(s, r, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// True when a caller who is not signed in holds <paramref name="permission"/> on a server without authentication:
    /// every permission but <see cref="SignInRequired"/>, unless <see cref="CliServerOptions.AllowAnonymousAdmin"/>.
    /// </summary>
    public static bool AnonymousCan(CliServerOptions options, string permission) =>
        RefusedAnonymously(options, [permission]).Count == 0;

    /// <summary>The error of a refused anonymous call: <c>sign-in required: plugins:install (...)</c>.</summary>
    public static string SignInRequiredMessage(IEnumerable<string> refused) =>
        $"sign-in required: {string.Join(", ", refused)} (protect the server with the users plugin, sign in to the admin " +
        "dashboard, or set AllowAnonymousAdmin)";

    /// <summary>The permissions of <paramref name="required"/> that <paramref name="principal"/> lacks.</summary>
    public static List<string> Missing(CliPrincipal principal, IEnumerable<string> required) =>
        CliPermissionMatcher.Missing(principal.Permissions, required);
}
