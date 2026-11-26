using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.Notifications;
using Qodalis.Cli.Abstractions.Restart;
using Qodalis.Cli.Plugins;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Notifications;

/// <summary>
/// The server's own notifications (the same keys, texts and resolution as the Node and Python servers): restart
/// required, the anonymous-admin security warning, and plugins that failed to load or need configuration at this start.
/// They need <c>admin:access</c>.
/// </summary>
public static class CliSystemNotifications
{
    /// <summary>The permission the server's own notifications need (the dashboard's audience).</summary>
    public const string Permission = CliRoutePolicy.AdminAccess;

    /// <summary>"Restart required" while changes wait for a restart; resolved once none do (so after every start).</summary>
    public static void SyncRestart(ICliNotifications notifications, IReadOnlyList<CliRestartRequirement> pending)
    {
        if (pending.Count == 0)
        {
            notifications.Resolve(CliNotificationKeys.RestartRequired);
            return;
        }
        var message = string.Join('\n', pending.Select(p => string.IsNullOrEmpty(p.Description) ? p.Id : p.Description));
        notifications.Notify(new CliNotificationInput
        {
            Key = CliNotificationKeys.RestartRequired,
            Kind = "restart",
            Severity = CliNotificationSeverities.Warning,
            Title = pending.Count == 1 ? "Restart required: 1 change waits for a restart" : $"Restart required: {pending.Count} changes wait for a restart",
            Message = message.Length > 2000 ? message[..2000] : message,
            Link = "/",
            RequiredPermission = Permission,
            Data = new JsonObject { ["pending"] = new JsonArray(pending.Select(p => (JsonNode?)p.Id).ToArray()) },
        });
    }

    /// <summary>The security warning while <c>AllowAnonymousAdmin</c> is on (on a server without sign-in); resolved when it is off.</summary>
    public static void SyncAnonymousAdmin(ICliNotifications notifications, bool on)
    {
        if (!on)
        {
            notifications.Resolve(CliNotificationKeys.AnonymousAdmin);
            return;
        }
        notifications.Notify(new CliNotificationInput
        {
            Key = CliNotificationKeys.AnonymousAdmin,
            Kind = "security",
            Severity = CliNotificationSeverities.Warning,
            Title = "Anyone can administer this server",
            Message = "AllowAnonymousAdmin is on and no sign-in protects the server: anyone who can reach it can install plugins (code that runs in the server), change its configuration and restart it. Protect it with the users plugin, or turn AllowAnonymousAdmin off.",
            Link = "/config",
            RequiredPermission = Permission,
        });
    }

    /// <summary>
    /// "Plugin failed to load" and "Plugin needs configuration" from what this start loaded; the notifications of plugins
    /// that load now (or are gone) are resolved.
    /// </summary>
    public static void SyncPlugins(ICliNotifications notifications, IReadOnlyList<CliServerPluginState> states)
    {
        var failed = new List<string>();
        var needs = new List<string>();
        foreach (var s in states)
        {
            if (s.Status == CliServerPluginStatuses.Failed)
            {
                var key = CliNotificationKeys.PluginFailedPrefix + s.Name;
                if (!CliNotificationInput.IsValidKey(key)) continue;
                failed.Add(key);
                var message = $"{s.Package}@{s.Version}: {s.Error ?? "unknown error"}";
                notifications.Notify(new CliNotificationInput
                {
                    Key = key,
                    Kind = "plugin",
                    Severity = CliNotificationSeverities.Error,
                    Title = Title($"Plugin {s.Name} failed to load"),
                    Message = message.Length > 2000 ? message[..2000] : message,
                    Link = $"/plugins/{Uri.EscapeDataString(s.Package)}",
                    RequiredPermission = Permission,
                    Data = new JsonObject
                    {
                        ["plugin"] = s.Name, ["package"] = s.Package, ["version"] = s.Version, ["source"] = s.Source, ["error"] = s.Error,
                    },
                });
            }
            else if (s.Status == CliServerPluginStatuses.NeedsConfiguration)
            {
                var key = CliNotificationKeys.PluginNeedsConfigurationPrefix + s.Name;
                if (!CliNotificationInput.IsValidKey(key)) continue;
                needs.Add(key);
                var missing = s.Missing ?? [];
                notifications.Notify(new CliNotificationInput
                {
                    Key = key,
                    Kind = "plugin",
                    Severity = CliNotificationSeverities.Warning,
                    Title = Title($"Plugin {s.Name} needs configuration"),
                    Message = $"Set {(missing.Count > 0 ? string.Join(", ", missing) : "its required settings")}, then restart the server.",
                    Link = $"/config?provider={Uri.EscapeDataString(s.Settings ?? s.Name)}",
                    RequiredPermission = Permission,
                    Data = new JsonObject
                    {
                        ["plugin"] = s.Name, ["package"] = s.Package, ["version"] = s.Version, ["source"] = s.Source,
                        ["missing"] = new JsonArray(missing.Select(m => (JsonNode?)m).ToArray()),
                    },
                });
            }
        }
        notifications.ResolveMissing(CliNotificationKeys.PluginFailedPrefix, failed);
        notifications.ResolveMissing(CliNotificationKeys.PluginNeedsConfigurationPrefix, needs);
    }

    private static string Title(string title) => title.Length > 200 ? title[..200] : title;
}
