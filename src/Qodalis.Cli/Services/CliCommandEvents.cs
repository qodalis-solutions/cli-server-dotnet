using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Services;

/// <summary>Publishes <c>command:executed</c> (audience <c>audit:read</c>) for each execution of a known command.</summary>
internal static class CliCommandEvents
{
    public const string ExecutedEvent = "command:executed";

    private static readonly CliEventAudience Audience = CliEventAudience.WithPermission(CliRoutePolicy.AuditRead);

    /// <summary>
    /// Publishes the execution of <paramref name="command"/>: only its processor name (the first word), never the
    /// arguments or input. Commands the registry does not know are not published (the name could be anything typed).
    /// </summary>
    public static Task PublishAsync(ICliEventBus? bus, string? command, bool known, bool success, TimeSpan duration,
        CliPrincipal? principal)
    {
        var name = CliCommandUsage.Key(command);
        if (bus == null || !known || name.Length == 0) return Task.CompletedTask;

        var data = new Dictionary<string, object?>
        {
            ["command"] = name,
            ["success"] = success,
            ["durationMs"] = (long)Math.Max(0, duration.TotalMilliseconds),
        };
        if (principal != null)
        {
            data["userId"] = principal.UserId;
            data["actor"] = principal.UserName;
        }
        return bus.PublishAsync(ExecutedEvent, data, Audience);
    }
}
