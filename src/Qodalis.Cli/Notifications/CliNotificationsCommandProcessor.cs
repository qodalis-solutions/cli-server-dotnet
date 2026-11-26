using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Abstractions.Notifications;

namespace Qodalis.Cli.Notifications;

/// <summary>
/// <c>notifications</c>: the caller's system notifications from the terminal. <c>list [--unread] [--severity s]</c>,
/// <c>read &lt;id&gt;|--all</c>, <c>clear &lt;id&gt; [--all]</c> (<c>--all</c> clears it for everyone and needs
/// <c>notifications:manage</c>). An id may be shortened to a unique prefix, or be the notification's key.
/// </summary>
public sealed class CliNotificationsCommandProcessor : CliCommandProcessor
{
    private readonly NotificationsListProcessor _list;

    /// <param name="service">The server's notifications.</param>
    /// <param name="viewer">The command's caller as a reader (the signed-in user, or the one reader of an open server).</param>
    public CliNotificationsCommandProcessor(CliNotificationService service, Func<CliNotificationViewer> viewer)
    {
        _list = new NotificationsListProcessor(service, viewer);
        Processors = [_list, new NotificationsReadProcessor(service, viewer), new NotificationsClearProcessor(service, viewer)];
    }

    public override string Command { get; set; } = "notifications";
    public override string Description { get; set; } = "System notifications: updates, restarts, plugin problems, security warnings";
    public override string? Module { get; set; } = "notifications";
    public override string? Icon { get; set; } = "🔔";

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        _list.HandleAsync(command, cancellationToken);

    private static readonly Dictionary<string, string> SeverityMark = new()
    {
        [CliNotificationSeverities.Info] = "i",
        [CliNotificationSeverities.Warning] = "!",
        [CliNotificationSeverities.Error] = "x",
    };

    /// <summary>One notification as a line: <c>* ! 2026-11-26 10:00  Restart required  (n_ab12…)</c>.</summary>
    public static string FormatLine(CliNotification n) =>
        $"{(n.Read == true ? " " : "*")} {SeverityMark.GetValueOrDefault(n.Severity, " ")} {ShortTime(n.UpdatedAt)}  {n.Title}  ({n.Id})";

    private static string ShortTime(string iso) => iso.Replace('T', ' ')[..Math.Min(16, iso.Length)];

    internal static bool Flag(CliProcessCommand command, string name) =>
        command.Args.TryGetValue(name, out var value) && value?.ToString() is "True" or "true" or "";

    /// <summary>A notification by its id, a unique id prefix or its key; else the error line.</summary>
    internal static (CliNotification? Found, string? Error) Find(CliNotificationService service, CliNotificationViewer viewer, string reference)
    {
        if (service.Find(viewer, reference) is { } exact) return (exact, null);
        var matches = service.List(viewer).Items.Where(n => n.Id.StartsWith(reference, StringComparison.Ordinal) || n.Key == reference).ToList();
        return matches.Count switch
        {
            1 => (matches[0], null),
            0 => (null, $"Error: notification not found: {reference}"),
            _ => (null, $"Error: {reference} matches {matches.Count} notifications; use the full id"),
        };
    }

    private sealed class NotificationsListProcessor(CliNotificationService service, Func<CliNotificationViewer> viewer) : CliCommandProcessor
    {
        public override string Command { get; set; } = "list";
        public override string Description { get; set; } = "Lists your notifications, newest first (* unread)";

        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
        [
            new CliCommandParameterDescriptor { Name = "unread", Description = "Only the unread ones", Type = CommandParameterType.Boolean },
            new CliCommandParameterDescriptor
            {
                Name = "severity", Description = "Only info, warning or error", Type = CommandParameterType.String,
                AllowedValues = [.. CliNotificationSeverities.All],
            },
        ];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        {
            var unread = Flag(command, "unread");
            var severity = command.Args.TryGetValue("severity", out var raw) ? raw?.ToString() : null;
            var list = service.List(viewer(), unread, string.IsNullOrEmpty(severity) ? null : severity);
            if (list.Items.Count == 0) return Task.FromResult(unread ? "No unread notifications." : "No notifications.");
            var lines = new List<string> { $"{list.Items.Count} notification(s), {list.UnreadCount} unread" };
            foreach (var n in list.Items)
            {
                lines.Add(FormatLine(n));
                if (n.Message.Length > 0) lines.Add($"      {n.Message.Replace("\n", "\n      ")}");
            }
            return Task.FromResult(string.Join('\n', lines));
        }
    }

    private sealed class NotificationsReadProcessor(CliNotificationService service, Func<CliNotificationViewer> viewer) : CliCommandProcessor
    {
        public override string Command { get; set; } = "read";
        public override string Description { get; set; } = "Marks a notification read (notifications read <id>), or all of them (--all)";
        public override bool? AcceptsRawInput { get; set; } = true;

        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
        [
            new CliCommandParameterDescriptor { Name = "all", Description = "Mark every notification read", Type = CommandParameterType.Boolean },
        ];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        {
            var reader = viewer();
            if (Flag(command, "all")) return Task.FromResult($"Marked {service.MarkAllRead(reader)} notification(s) read.");
            var reference = CliValueHelpers.GetPositionals(command).FirstOrDefault();
            if (string.IsNullOrEmpty(reference)) return Task.FromResult("Error: usage: notifications read <id> | notifications read --all");
            var (found, error) = Find(service, reader, reference);
            if (found == null) return Task.FromResult(error!);
            service.MarkRead(reader, found.Id);
            return Task.FromResult($"Read: {found.Title}");
        }
    }

    private sealed class NotificationsClearProcessor(CliNotificationService service, Func<CliNotificationViewer> viewer) : CliCommandProcessor
    {
        public override string Command { get; set; } = "clear";
        public override string Description { get; set; } =
            $"Dismisses a notification for you (notifications clear <id>); --all clears it for everyone ({CliNotificationConstants.ManagePermission})";
        public override bool? ValueRequired { get; set; } = true;

        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
        [
            new CliCommandParameterDescriptor
            {
                Name = "all", Description = $"Clear it for every user ({CliNotificationConstants.ManagePermission})", Type = CommandParameterType.Boolean,
            },
        ];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        {
            var reader = viewer();
            var reference = CliValueHelpers.GetPositionals(command).FirstOrDefault();
            if (string.IsNullOrEmpty(reference)) return Task.FromResult("Error: usage: notifications clear <id> [--all]");
            var all = Flag(command, "all");
            if (all && !reader.Can(CliNotificationConstants.ManagePermission))
                return Task.FromResult($"Error: forbidden: missing permission {CliNotificationConstants.ManagePermission}");
            var (found, error) = Find(service, reader, reference);
            if (found == null) return Task.FromResult(error!);
            if (all)
            {
                service.Clear(reader, found.Id);
                return Task.FromResult($"Cleared for everyone: {found.Title}");
            }
            service.Dismiss(reader, found.Id);
            return Task.FromResult($"Dismissed: {found.Title}");
        }
    }
}
