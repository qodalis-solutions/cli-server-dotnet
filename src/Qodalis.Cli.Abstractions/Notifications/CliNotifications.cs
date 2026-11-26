using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Qodalis.Cli.Abstractions.Notifications;

// System notifications: what the server tells its users and admins (an update is available, a restart is required, a
// plugin failed to load or needs configuration, a security warning). The same contract, routes and JSON in the Node,
// .NET and Python servers (docs/notifications-spec.md of @qodalis/cli-server-node).
//
// A source publishes with Notify(new() { Key = ..., ... }): the key names the condition, so publishing the same key again
// updates the notification instead of adding one, and Resolve(key) removes it once the condition ended (the update was
// applied, the server restarted). Plugins publish through CliPluginContext.Notifications, the host through
// CliBuilder.Notifications or ICliNotifications in the container.

/// <summary>Names of the notifications feature.</summary>
public static class CliNotificationConstants
{
    /// <summary>Published on the event bus (and the events socket) when a notification changes.</summary>
    public const string ChangedEvent = "notification:changed";

    /// <summary>Clearing a notification for everyone (<c>DELETE /api/v1/qcli/notifications/{id}?scope=all</c>, <c>notifications clear --all</c>).</summary>
    public const string ManagePermission = "notifications:manage";

    /// <summary>Seeing the available updates and checking for them (<c>/api/v1/qcli/updates</c>).</summary>
    public const string UpdatesReadPermission = "updates:read";

    /// <summary>The route of the notifications API.</summary>
    public const string Path = "/api/v1/qcli/notifications";

    /// <summary>The reader id of every caller on a server without sign-in (one shared read state).</summary>
    public const string AnonymousReader = "anonymous";
}

/// <summary>How serious a notification is.</summary>
public static class CliNotificationSeverities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";

    /// <summary>The severities, mildest first.</summary>
    public static readonly IReadOnlyList<string> All = [Info, Warning, Error];
}

/// <summary>What <c>notification:changed</c> says happened.</summary>
public static class CliNotificationChanges
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Resolved = "resolved";
    public const string Cleared = "cleared";
    public const string Read = "read";
    public const string ReadAll = "read-all";
    public const string Dismissed = "dismissed";
}

/// <summary>The keys of the server's own sources (the same on every platform).</summary>
public static class CliNotificationKeys
{
    /// <summary>Changes wait for a restart.</summary>
    public const string RestartRequired = "server.restart-required";

    /// <summary><c>AllowAnonymousAdmin</c> is on.</summary>
    public const string AnonymousAdmin = "security.anonymous-admin";

    /// <summary><c>plugin.failed:&lt;plugin name&gt;</c>: a plugin failed to load at the last start.</summary>
    public const string PluginFailedPrefix = "plugin.failed:";

    /// <summary><c>plugin.needs-configuration:&lt;plugin name&gt;</c>: required settings without a value.</summary>
    public const string PluginNeedsConfigurationPrefix = "plugin.needs-configuration:";

    /// <summary><c>update:dashboard:&lt;name&gt;</c>, <c>update:server:&lt;name&gt;</c>, <c>update:code:&lt;kind&gt;:&lt;package&gt;</c>, <c>update:core:&lt;package&gt;</c>.</summary>
    public const string UpdatePrefix = "update:";
}

/// <summary>What a source publishes (<see cref="ICliNotifications.Notify"/>).</summary>
public sealed class CliNotificationInput
{
    /// <summary>
    /// Stable id of the condition (<c>update:server:weather</c>, <c>server.restart-required</c>): the same key updates the
    /// notification, <see cref="ICliNotifications.Resolve"/> removes it. <c>[A-Za-z0-9._:@/+-]</c>, 1-200 characters.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>What it is about, for filters and icons: <c>update</c>, <c>restart</c>, <c>plugin</c>, <c>security</c>, a plugin's own kind.</summary>
    public required string Kind { get; init; }

    /// <summary><see cref="CliNotificationSeverities"/>: <c>info</c>, <c>warning</c> or <c>error</c>.</summary>
    public required string Severity { get; init; }

    /// <summary>One line (at most 200 characters).</summary>
    public required string Title { get; init; }

    /// <summary>Details (at most 2000 characters).</summary>
    public string Message { get; init; } = "";

    /// <summary>A dashboard path (<c>/plugins?tab=updates</c>) or an absolute http(s) URL the notification opens.</summary>
    public string? Link { get; init; }

    /// <summary>Only users holding this permission see it (on a server without sign-in everyone sees everything).</summary>
    public string? RequiredPermission { get; init; }

    /// <summary>Machine-readable details (at most 8 KB of JSON).</summary>
    public JsonObject? Data { get; init; }

    private static readonly Regex KeyPattern = new("^[A-Za-z0-9._:@/+-]{1,200}$", RegexOptions.CultureInvariant);
    private static readonly Regex KindPattern = new("^[a-z0-9][a-z0-9._-]{0,39}$", RegexOptions.CultureInvariant);
    private static readonly Regex PermissionPattern = new("^[A-Za-z0-9_.*-]+:[A-Za-z0-9_.*-]+$", RegexOptions.CultureInvariant);

    /// <summary>The largest <see cref="Data"/>, as JSON.</summary>
    public const int MaxDataBytes = 8192;

    /// <summary>True for a valid notification key.</summary>
    public static bool IsValidKey(string? key) => key != null && KeyPattern.IsMatch(key);

    /// <summary>
    /// Checks and normalizes the input (trimmed kind, title and message, empty optional fields left out, a copy of
    /// <see cref="Data"/>). Throws <see cref="ArgumentException"/> naming the field of an invalid one.
    /// </summary>
    public CliNotificationInput Normalize()
    {
        if (!IsValidKey(Key)) throw new ArgumentException("notification key must be 1-200 characters of [A-Za-z0-9._:@/+-]", nameof(Key));
        var kind = Kind?.Trim() ?? "";
        if (!KindPattern.IsMatch(kind)) throw new ArgumentException("notification kind must be 1-40 characters of [a-z0-9._-]", nameof(Kind));
        if (Severity == null || !CliNotificationSeverities.All.Contains(Severity))
            throw new ArgumentException("notification severity must be info, warning or error", nameof(Severity));
        var title = Title?.Trim() ?? "";
        if (title.Length is 0 or > 200) throw new ArgumentException("notification title must be 1-200 characters", nameof(Title));
        var message = Message?.Trim() ?? "";
        if (message.Length > 2000) throw new ArgumentException("notification message must be at most 2000 characters", nameof(Message));
        string? link = null;
        if (!string.IsNullOrEmpty(Link))
        {
            var path = Link.Length > 1 && Link[0] == '/' && Link[1] != '/' || Link == "/";
            var url = Link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || Link.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (Link.Length > 2000 || !(path || url)) throw new ArgumentException("notification link must be a path starting with / or an http(s) URL", nameof(Link));
            link = Link;
        }
        string? permission = null;
        if (!string.IsNullOrEmpty(RequiredPermission))
        {
            if (!PermissionPattern.IsMatch(RequiredPermission))
                throw new ArgumentException("notification requiredPermission must be resource:action", nameof(RequiredPermission));
            permission = RequiredPermission;
        }
        JsonObject? data = null;
        if (Data != null)
        {
            var json = Data.ToJsonString();
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxDataBytes)
                throw new ArgumentException("notification data must be at most 8 KB of JSON", nameof(Data));
            data = JsonNode.Parse(json)!.AsObject();
        }
        return new CliNotificationInput
        {
            Key = Key!, Kind = kind, Severity = Severity, Title = title, Message = message, Link = link, RequiredPermission = permission, Data = data,
        };
    }
}

/// <summary>A notification as a user sees it (<c>GET /api/v1/qcli/notifications</c>); optional fields left out when unset.</summary>
public sealed class CliNotification
{
    /// <summary>Server-generated id (<c>n_</c> + 16 lowercase hex characters), kept while the key lives.</summary>
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("key")] public required string Key { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("severity")] public required string Severity { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }

    [JsonPropertyName("link")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Link { get; init; }

    [JsonPropertyName("requiredPermission")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequiredPermission { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Data { get; init; }

    /// <summary>When the key was first published (ISO 8601 UTC).</summary>
    [JsonPropertyName("createdAt")] public required string CreatedAt { get; init; }

    /// <summary>When its content last changed (ISO 8601 UTC): a change makes it unread again.</summary>
    [JsonPropertyName("updatedAt")] public required string UpdatedAt { get; init; }

    /// <summary>The caller has read it; null where no caller is involved (<see cref="ICliNotifications.Get"/>, the event before it is personalised).</summary>
    [JsonPropertyName("read")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Read { get; init; }

    /// <summary>A copy with <see cref="Read"/> set (and its own copy of <see cref="Data"/>).</summary>
    public CliNotification WithRead(bool? read) => new()
    {
        Id = Id, Key = Key, Kind = Kind, Severity = Severity, Title = Title, Message = Message, Link = Link,
        RequiredPermission = RequiredPermission, Data = Data?.DeepClone().AsObject(), CreatedAt = CreatedAt, UpdatedAt = UpdatedAt, Read = read,
    };
}

/// <summary><c>GET /api/v1/qcli/notifications</c>.</summary>
public sealed class CliNotificationList
{
    [JsonPropertyName("items")] public required IReadOnlyList<CliNotification> Items { get; init; }

    /// <summary>The caller's unread notifications (all of them, whatever the filters).</summary>
    [JsonPropertyName("unreadCount")] public int UnreadCount { get; init; }
}

/// <summary>
/// The server's notifications (<c>CliBuilder.Notifications</c>, <c>CliPluginContext.Notifications</c>, in the container).
/// </summary>
public interface ICliNotifications
{
    /// <summary>
    /// Adds the notification, or updates the one with the same key: identical content changes nothing; changed content
    /// is unread (and shown) again for everyone. Throws <see cref="ArgumentException"/> for invalid input.
    /// </summary>
    CliNotification Notify(CliNotificationInput input);

    /// <summary>Removes the notification of a key whose condition ended; true when there was one.</summary>
    bool Resolve(string key);

    /// <summary>
    /// Removes the notifications whose key starts with <paramref name="prefix"/> and is not in <paramref name="keep"/>: a
    /// source that re-evaluates its conditions (at startup, after a check) resolves what it no longer sees. Returns how
    /// many were removed.
    /// </summary>
    int ResolveMissing(string prefix, IEnumerable<string> keep);

    /// <summary>The notification of a key (no per-user state: <see cref="CliNotification.Read"/> is null), null when none.</summary>
    CliNotification? Get(string key);
}

// ------------------------------------------------------------------------ updates

/// <summary>Where an update comes from and how it is applied.</summary>
public static class CliUpdateSources
{
    /// <summary>Installed from the marketplace: updated from the dashboard or <c>admin ... update</c>.</summary>
    public const string Marketplace = "marketplace";

    /// <summary>Added in the host's code: updated there.</summary>
    public const string Code = "code";

    /// <summary>The .NET server package, read from NuGet (the Node server says <c>npm</c>, the Python server <c>pypi</c>).</summary>
    public const string NuGet = "nuget";
}

/// <summary>One package with a newer version (<c>GET /api/v1/qcli/updates</c>).</summary>
public sealed class CliUpdateItem
{
    /// <summary><c>dashboard</c> plugin, <c>server</c> plugin, or the server's <c>core</c> package.</summary>
    [JsonPropertyName("kind")] public required string Kind { get; init; }

    /// <summary>The marketplace name (<c>weather</c>, <c>@qodalis/cli-dashboard-module-tickets</c>) or the package.</summary>
    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>The package of this server's platform.</summary>
    [JsonPropertyName("package")] public required string Package { get; init; }

    [JsonPropertyName("title")] public required string Title { get; init; }

    [JsonPropertyName("icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Icon { get; init; }

    /// <summary>The version running or installed; null when unknown.</summary>
    [JsonPropertyName("current")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Current { get; set; }

    /// <summary>The newest version this server can use; null when none.</summary>
    [JsonPropertyName("latest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Latest { get; init; }

    /// <summary><see cref="CliUpdateSources"/>.</summary>
    [JsonPropertyName("source")] public required string Source { get; init; }

    /// <summary>A newer version exists.</summary>
    [JsonPropertyName("updateAvailable")] public bool UpdateAvailable { get; set; }

    /// <summary>It can be updated from the dashboard or <c>admin ... update</c> (marketplace installs the policy allows).</summary>
    [JsonPropertyName("canUpdate")] public bool CanUpdate { get; init; }

    /// <summary>Why it cannot be updated here (<c>update in code</c>, <c>unverified publisher</c>, ...).</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>The install command per platform (<c>{ dotnet: "dotnet add package … --version 1.2.0" }</c>), for updates made in code.</summary>
    [JsonPropertyName("install")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Install { get; init; }

    /// <summary>A deep copy.</summary>
    public CliUpdateItem Clone() => JsonSerializer.Deserialize<CliUpdateItem>(JsonSerializer.Serialize(this))!;
}

/// <summary><c>GET /api/v1/qcli/updates</c>: the update checker's last result.</summary>
public sealed class CliUpdatesResult
{
    /// <summary>When the last check finished (ISO 8601 UTC); null before the first.</summary>
    [JsonPropertyName("checkedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? CheckedAt { get; set; }

    /// <summary>A check is running.</summary>
    [JsonPropertyName("checking")] public bool Checking { get; set; }

    /// <summary>Why the last check failed, partly or fully (<c>dashboard plugins: marketplace unreachable</c>).</summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    /// <summary>The server package: always listed once checked, <c>updateAvailable</c> false when current.</summary>
    [JsonPropertyName("server")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public CliUpdateItem? Server { get; set; }

    /// <summary>Dashboard plugins installed from the marketplace with a newer version.</summary>
    [JsonPropertyName("dashboardPlugins")] public List<CliUpdateItem> DashboardPlugins { get; set; } = [];

    /// <summary>Server plugins installed from the marketplace with a newer version.</summary>
    [JsonPropertyName("serverPlugins")] public List<CliUpdateItem> ServerPlugins { get; set; } = [];

    /// <summary>Plugins (dashboard and server) the host adds in code whose package has a newer version: update them in code.</summary>
    [JsonPropertyName("codePlugins")] public List<CliUpdateItem> CodePlugins { get; set; } = [];

    /// <summary>The updates available, all lists and the server together (the dashboard's badge).</summary>
    [JsonPropertyName("count")] public int Count { get; set; }

    /// <summary>The three lists plus 1 when the server has an update.</summary>
    public int CountUpdates() => DashboardPlugins.Count + ServerPlugins.Count + CodePlugins.Count + (Server?.UpdateAvailable == true ? 1 : 0);

    /// <summary>A deep copy.</summary>
    public CliUpdatesResult Clone() => JsonSerializer.Deserialize<CliUpdatesResult>(JsonSerializer.Serialize(this))!;
}
