using Qodalis.Cli.Notifications;

namespace Qodalis.Cli.Extensions;

public partial class CliBuilder
{
    /// <summary>Where the system notifications are kept and how many (<see cref="ConfigureNotifications"/>).</summary>
    public CliNotificationsOptions NotificationsOptions { get; } = new();

    /// <summary>
    /// The server's system notifications (<c>GET /api/v1/qcli/notifications</c>, the dashboard's bell, the
    /// <c>notifications</c> command): <c>Notify(new() { Key, Kind, Severity, Title, Message, Link?, RequiredPermission? })</c>
    /// adds or updates the notification of a key, <c>Resolve(key)</c> removes it once the condition ended. Usable from
    /// the host's code and plugins' registrations (events start once the server runs); the same service is
    /// <see cref="Qodalis.Cli.Abstractions.Notifications.ICliNotifications"/> and <see cref="CliNotificationService"/> in the container.
    /// Kept in <c>&lt;data dir&gt;/notifications.json</c>.
    /// </summary>
    public CliNotificationService Notifications { get; } = new();

    /// <summary>
    /// Where the notifications are kept (<c>File</c>, default <c>&lt;data dir&gt;/notifications.json</c>; <c>Persist = false</c>
    /// keeps them in memory) and how many: <c>MaxItems</c> (500), <c>MaxAgeDays</c> after their last change (30).
    /// Throws <see cref="ArgumentException"/> for an invalid limit.
    /// </summary>
    /// <example>
    /// <code>
    /// cli.ConfigureNotifications(o =&gt; o.MaxAgeDays = 7);
    /// </code>
    /// </example>
    public CliBuilder ConfigureNotifications(Action<CliNotificationsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(NotificationsOptions);
        NotificationsOptions.Validate();
        return this;
    }
}
