using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Abstractions.Notifications;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Notifications;

/// <summary>Where the notifications are kept and how many (<c>cli.ConfigureNotifications(o =&gt; ...)</c>).</summary>
public sealed class CliNotificationsOptions
{
    /// <summary>The file below the data directory.</summary>
    public const string FileName = "notifications.json";

    /// <summary>The most notifications kept unless set.</summary>
    public const int DefaultMaxItems = 500;

    /// <summary>Days a notification is kept after its last change unless set.</summary>
    public const double DefaultMaxAgeDays = 30;

    /// <summary>Keep them in a file (default true); false keeps them in memory only (lost on a restart).</summary>
    public bool Persist { get; set; } = true;

    /// <summary>The file (default <c>&lt;data dir&gt;/notifications.json</c>, the server plugins' data directory).</summary>
    public string? File { get; set; }

    /// <summary>The most notifications kept (default 500; the oldest change dropped first).</summary>
    public int MaxItems { get; set; } = DefaultMaxItems;

    /// <summary>Days a notification is kept after its last change (default 30).</summary>
    public double MaxAgeDays { get; set; } = DefaultMaxAgeDays;

    /// <summary>Throws <see cref="ArgumentException"/> for a limit below 1 or an age that is not positive.</summary>
    public void Validate()
    {
        if (MaxItems < 1) throw new ArgumentException("notifications MaxItems must be at least 1", nameof(MaxItems));
        if (!(MaxAgeDays > 0) || double.IsInfinity(MaxAgeDays)) throw new ArgumentException("notifications MaxAgeDays must be positive", nameof(MaxAgeDays));
    }
}

/// <summary>Who reads: their id (the read state) and what they may see.</summary>
/// <param name="UserId">The reader's id; <see cref="CliNotificationConstants.AnonymousReader"/> on a server without sign-in.</param>
/// <param name="Can">True when the reader holds the permission (always on a server without sign-in).</param>
public sealed record CliNotificationViewer(string UserId, Func<string, bool> Can)
{
    /// <summary>The one reader of a server without sign-in: sees everything.</summary>
    public static CliNotificationViewer Anonymous { get; } = new(CliNotificationConstants.AnonymousReader, _ => true);

    /// <summary>
    /// The reader of a caller: the signed-in user on a protected server (their permissions, wildcards included); on a
    /// server without sign-in, or without a caller, <see cref="Anonymous"/>.
    /// </summary>
    public static CliNotificationViewer Of(CliPrincipal? principal, CliServerOptions? options) =>
        options?.RequireAuthentication == true && principal != null ? new(principal.UserId, principal.Can) : Anonymous;
}

/// <summary>
/// The server's notifications (<c>CliBuilder.Notifications</c>, <see cref="ICliNotifications"/> in the container): sources
/// publish with <see cref="Notify"/> (a key updates its notification) and <see cref="Resolve"/> once the condition ended;
/// users list, read and dismiss theirs. Kept in <c>&lt;data dir&gt;/notifications.json</c> (written atomically on every
/// change; at most 500, 30 days after their last change) with each user's read and dismissed state; every change is
/// published as <c>notification:changed</c> (audience: the notification's permission, or the user for read and dismiss).
/// </summary>
public sealed class CliNotificationService : ICliNotifications
{
    private const int FileVersion = 1;

    private sealed class Stored
    {
        public required string Id { get; init; }
        public required string Key { get; init; }
        public required string Kind { get; set; }
        public required string Severity { get; set; }
        public required string Title { get; set; }
        public required string Message { get; set; }
        public string? Link { get; set; }
        public string? RequiredPermission { get; set; }
        public JsonObject? Data { get; set; }
        public required string CreatedAt { get; init; }
        public required string UpdatedAt { get; set; }
        public List<string> ReadBy { get; set; } = [];
        public List<string> DismissedBy { get; set; } = [];
    }

    private readonly object _lock = new();
    private List<Stored> _items = [];
    private string? _file;
    private int _maxItems = CliNotificationsOptions.DefaultMaxItems;
    private TimeSpan _maxAge = TimeSpan.FromDays(CliNotificationsOptions.DefaultMaxAgeDays);

    /// <summary>
    /// A service with <paramref name="options"/> (its <c>File</c> as given: none, or <c>Persist = false</c>, keeps them in
    /// memory), publishing on <paramref name="eventBus"/>.
    /// </summary>
    public CliNotificationService(CliNotificationsOptions? options = null, Func<ICliEventBus?>? eventBus = null, TimeProvider? time = null,
        ILogger? logger = null)
    {
        EventBus = eventBus;
        Time = time ?? TimeProvider.System;
        Logger = logger ?? NullLogger.Instance;
        if (options != null) Configure(options);
    }

    /// <summary>The bus the <c>notification:changed</c> events go to (the server's once it started); none: no events.</summary>
    public Func<ICliEventBus?>? EventBus { get; set; }

    /// <summary>The clock of <c>createdAt</c>, <c>updatedAt</c> and the retention.</summary>
    public TimeProvider Time { get; set; }

    public ILogger Logger { get; set; }

    /// <summary>The file they are kept in; null in memory.</summary>
    public string? FilePath
    {
        get
        {
            lock (_lock) return _file;
        }
    }

    /// <summary>
    /// Sets the limits and where they are kept (<c>AddCli</c> calls it once, before plugins publish). A new file's
    /// notifications are loaded; those published before (in memory) win over the file's of the same key.
    /// </summary>
    public void Configure(CliNotificationsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var file = options.Persist && !string.IsNullOrWhiteSpace(options.File) ? Path.GetFullPath(options.File) : null;
        lock (_lock)
        {
            _maxItems = options.MaxItems;
            _maxAge = TimeSpan.FromDays(options.MaxAgeDays);
            // Published before the file was known (the host's code, a plugin's registration): written to it now
            var unsaved = false;
            if (file != _file)
            {
                _file = file;
                var mine = _items.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
                unsaved = mine.Count > 0;
                _items = [.. _items, .. ReadFile().Where(n => !mine.Contains(n.Key))];
            }
            if (Prune() | unsaved) Save();
        }
    }

    // ---------------------------------------------------------------- sources

    public CliNotification Notify(CliNotificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var n = input.Normalize();
        string action;
        Stored stored;
        lock (_lock)
        {
            var now = Now();
            var existing = _items.FirstOrDefault(x => x.Key == n.Key);
            if (existing != null)
            {
                if (ContentOf(existing.Kind, existing.Severity, existing.Title, existing.Message, existing.Link, existing.RequiredPermission, existing.Data)
                    == ContentOf(n.Kind, n.Severity, n.Title, n.Message, n.Link, n.RequiredPermission, n.Data))
                    return View(existing, null);
                existing.Kind = n.Kind;
                existing.Severity = n.Severity;
                existing.Title = n.Title;
                existing.Message = n.Message;
                existing.Link = n.Link;
                existing.RequiredPermission = n.RequiredPermission;
                existing.Data = n.Data;
                existing.UpdatedAt = now;
                // Changed: unread (and shown) again for everyone
                existing.ReadBy = [];
                existing.DismissedBy = [];
                stored = existing;
                action = CliNotificationChanges.Updated;
            }
            else
            {
                stored = new Stored
                {
                    Id = $"n_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}",
                    Key = n.Key, Kind = n.Kind, Severity = n.Severity, Title = n.Title, Message = n.Message, Link = n.Link,
                    RequiredPermission = n.RequiredPermission, Data = n.Data, CreatedAt = now, UpdatedAt = now,
                };
                _items.Add(stored);
                Prune();
                action = CliNotificationChanges.Created;
            }
            Save();
        }
        var view = View(stored, null);
        Publish(action, view, null);
        return view;
    }

    public bool Resolve(string key)
    {
        Stored? removed;
        lock (_lock)
        {
            removed = _items.FirstOrDefault(x => x.Key == key);
            if (removed == null) return false;
            _items.Remove(removed);
            Save();
        }
        Publish(CliNotificationChanges.Resolved, View(removed, null), null);
        return true;
    }

    public int ResolveMissing(string prefix, IEnumerable<string> keep)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var kept = (keep ?? []).ToHashSet(StringComparer.Ordinal);
        List<string> gone;
        lock (_lock) gone = _items.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal) && !kept.Contains(x.Key)).Select(x => x.Key).ToList();
        return gone.Count(Resolve);
    }

    public CliNotification? Get(string key)
    {
        lock (_lock)
        {
            var n = _items.FirstOrDefault(x => x.Key == key);
            return n == null ? null : View(n, null);
        }
    }

    // ---------------------------------------------------------------- readers

    /// <summary>What the viewer sees, newest first; <paramref name="unread"/> only the unread ones, <paramref name="severity"/> only those, at most <paramref name="limit"/>.</summary>
    public CliNotificationList List(CliNotificationViewer viewer, bool unread = false, string? severity = null, int? limit = null)
    {
        lock (_lock)
        {
            var visible = VisibleTo(viewer);
            IEnumerable<CliNotification> items = visible.Select(n => View(n, viewer.UserId));
            if (unread) items = items.Where(n => n.Read != true);
            if (!string.IsNullOrEmpty(severity)) items = items.Where(n => n.Severity == severity);
            if (limit is >= 0) items = items.Take(limit.Value);
            return new CliNotificationList { Items = items.ToList(), UnreadCount = visible.Count(n => !n.ReadBy.Contains(viewer.UserId)) };
        }
    }

    /// <summary>The viewer's unread notifications.</summary>
    public int UnreadCount(CliNotificationViewer viewer)
    {
        lock (_lock) return VisibleTo(viewer).Count(n => !n.ReadBy.Contains(viewer.UserId));
    }

    /// <summary>One notification as the viewer sees it; null when it does not exist, is hidden from them or was dismissed.</summary>
    public CliNotification? Find(CliNotificationViewer viewer, string id)
    {
        lock (_lock)
        {
            var n = VisibleTo(viewer).FirstOrDefault(x => x.Id == id);
            return n == null ? null : View(n, viewer.UserId);
        }
    }

    /// <summary>Marks one read for the viewer; null when they cannot see it.</summary>
    public CliNotification? MarkRead(CliNotificationViewer viewer, string id)
    {
        Stored? n;
        var changed = false;
        lock (_lock)
        {
            n = VisibleTo(viewer).FirstOrDefault(x => x.Id == id);
            if (n == null) return null;
            if (!n.ReadBy.Contains(viewer.UserId))
            {
                n.ReadBy.Add(viewer.UserId);
                changed = true;
                Save();
            }
        }
        if (changed) Publish(CliNotificationChanges.Read, View(n, null), viewer.UserId);
        return View(n, viewer.UserId);
    }

    /// <summary>Marks every notification the viewer sees read; returns how many were unread.</summary>
    public int MarkAllRead(CliNotificationViewer viewer)
    {
        int count;
        lock (_lock)
        {
            var unread = VisibleTo(viewer).Where(n => !n.ReadBy.Contains(viewer.UserId)).ToList();
            foreach (var n in unread) n.ReadBy.Add(viewer.UserId);
            count = unread.Count;
            if (count > 0) Save();
        }
        if (count > 0) Publish(CliNotificationChanges.ReadAll, null, viewer.UserId);
        return count;
    }

    /// <summary>Hides one from the viewer (it comes back when its content changes); false when they cannot see it.</summary>
    public bool Dismiss(CliNotificationViewer viewer, string id)
    {
        Stored? n;
        lock (_lock)
        {
            n = VisibleTo(viewer).FirstOrDefault(x => x.Id == id);
            if (n == null) return false;
            n.DismissedBy.Add(viewer.UserId);
            if (!n.ReadBy.Contains(viewer.UserId)) n.ReadBy.Add(viewer.UserId);
            Save();
        }
        Publish(CliNotificationChanges.Dismissed, View(n, null), viewer.UserId);
        return true;
    }

    /// <summary>Removes one for everyone (the caller checks <c>notifications:manage</c>); false when the viewer cannot see it.</summary>
    public bool Clear(CliNotificationViewer viewer, string id)
    {
        Stored? removed;
        lock (_lock)
        {
            Prune();
            removed = _items.FirstOrDefault(x => x.Id == id && CanSee(viewer, x));
            if (removed == null) return false;
            _items.Remove(removed);
            Save();
        }
        Publish(CliNotificationChanges.Cleared, View(removed, null), null);
        return true;
    }

    /// <summary>Whether <paramref name="userId"/> read the notification <paramref name="id"/> (the events socket personalises each message with it).</summary>
    public bool ReadFlag(string? id, string userId)
    {
        lock (_lock) return id != null && _items.FirstOrDefault(x => x.Id == id) is { } n && n.ReadBy.Contains(userId);
    }

    /// <summary>
    /// The <c>notification:changed</c> message of one events socket: <c>unreadCount</c> that reader's, <c>item.read</c> that
    /// reader's read flag (a hook of the events socket manager).
    /// </summary>
    public CliEvent Personalize(CliEvent cliEvent, CliNotificationViewer viewer)
    {
        var data = new Dictionary<string, object?>(cliEvent.Data);
        if (data.TryGetValue("item", out var value) && value is CliNotification item)
            data["item"] = item.WithRead(ReadFlag(item.Id, viewer.UserId));
        data["unreadCount"] = UnreadCount(viewer);
        return new CliEvent { Type = cliEvent.Type, Timestamp = cliEvent.Timestamp, Data = data, Audience = cliEvent.Audience };
    }

    // ---------------------------------------------------------------- internals

    private string Now() => Format(Time.GetUtcNow());

    private static string Format(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string ContentOf(string kind, string severity, string title, string message, string? link, string? permission, JsonObject? data) =>
        new JsonArray(kind, severity, title, message, link, permission, data?.DeepClone()).ToJsonString();

    private static bool CanSee(CliNotificationViewer viewer, Stored n) => n.RequiredPermission == null || viewer.Can(n.RequiredPermission);

    /// <summary>What the viewer sees, newest change first. Call with the lock held.</summary>
    private List<Stored> VisibleTo(CliNotificationViewer viewer)
    {
        if (Prune()) Save();
        return _items
            .Where(n => CanSee(viewer, n) && !n.DismissedBy.Contains(viewer.UserId))
            .OrderByDescending(n => n.UpdatedAt, StringComparer.Ordinal)
            .ThenByDescending(n => n.CreatedAt, StringComparer.Ordinal)
            .ToList();
    }

    private static CliNotification View(Stored n, string? userId) => new()
    {
        Id = n.Id, Key = n.Key, Kind = n.Kind, Severity = n.Severity, Title = n.Title, Message = n.Message, Link = n.Link,
        RequiredPermission = n.RequiredPermission, Data = n.Data?.DeepClone().AsObject(), CreatedAt = n.CreatedAt, UpdatedAt = n.UpdatedAt,
        Read = userId == null ? null : n.ReadBy.Contains(userId),
    };

    /// <summary>Drops the notifications past their age and over the cap (oldest change first); true when it dropped some. Call with the lock held.</summary>
    private bool Prune()
    {
        var before = _items.Count;
        var cutoff = Time.GetUtcNow() - _maxAge;
        _items = _items.Where(n => !DateTimeOffset.TryParse(n.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) || at >= cutoff).ToList();
        if (_items.Count > _maxItems)
            _items = _items.OrderByDescending(n => n.UpdatedAt, StringComparer.Ordinal).Take(_maxItems).ToList();
        return _items.Count != before;
    }

    private void Publish(string action, CliNotification? item, string? userId)
    {
        var bus = EventBus?.Invoke();
        if (bus == null) return;
        var data = new Dictionary<string, object?> { ["action"] = action };
        if (item != null)
        {
            data["id"] = item.Id;
            data["key"] = item.Key;
            if (action is not (CliNotificationChanges.Resolved or CliNotificationChanges.Cleared)) data["item"] = item;
        }
        // Read and dismiss concern one user; the rest everyone who may see the notification
        var audience = userId != null ? CliEventAudience.User(userId)
            : item?.RequiredPermission is { } permission ? CliEventAudience.WithPermission(permission)
            : CliEventAudience.Everyone;
        try
        {
            bus.PublishAsync(CliNotificationConstants.ChangedEvent, data, audience)
                .ContinueWith(t => Logger.LogError(t.Exception?.GetBaseException(), "Publishing {Type} failed", CliNotificationConstants.ChangedEvent),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Publishing {Type} failed", CliNotificationConstants.ChangedEvent);
        }
    }

    // ---------------------------------------------------------------- file

    private List<Stored> ReadFile()
    {
        if (_file == null || !File.Exists(_file)) return [];
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_file));
            if (root?["items"] is not JsonArray list) return [];
            var items = new List<Stored>();
            foreach (var node in list)
            {
                // A damaged entry is dropped
                if (node is not JsonObject raw) continue;
                try
                {
                    var n = new CliNotificationInput
                    {
                        Key = Str(raw, "key")!, Kind = Str(raw, "kind")!, Severity = Str(raw, "severity")!, Title = Str(raw, "title")!,
                        Message = Str(raw, "message") ?? "", Link = Str(raw, "link"), RequiredPermission = Str(raw, "requiredPermission"),
                        Data = raw["data"] as JsonObject,
                    }.Normalize();
                    if (Str(raw, "id") is not { } id || Str(raw, "createdAt") is not { } created || Str(raw, "updatedAt") is not { } updated) continue;
                    if (items.Any(x => x.Key == n.Key)) continue;
                    items.Add(new Stored
                    {
                        Id = id, Key = n.Key, Kind = n.Kind, Severity = n.Severity, Title = n.Title, Message = n.Message, Link = n.Link,
                        RequiredPermission = n.RequiredPermission, Data = n.Data, CreatedAt = created, UpdatedAt = updated,
                        ReadBy = Strings(raw["readBy"]), DismissedBy = Strings(raw["dismissedBy"]),
                    });
                }
                catch (Exception ex) when (ex is ArgumentException or NullReferenceException or InvalidOperationException)
                {
                    // dropped
                }
            }
            return items;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning("Reading {File} failed, starting without its notifications: {Error}", _file, ex.Message);
            return [];
        }

        static string? Str(JsonObject o, string key) =>
            o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

        static List<string> Strings(JsonNode? node) =>
            node is JsonArray a ? a.OfType<JsonValue>().Where(v => v.GetValueKind() == JsonValueKind.String).Select(v => v.GetValue<string>()).Distinct().ToList() : [];
    }

    /// <summary>Writes the file atomically (a temporary file renamed over it). Call with the lock held.</summary>
    private void Save()
    {
        if (_file == null) return;
        var file = _file;
        var tmp = $"{file}.{Environment.ProcessId}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var items = new JsonArray();
            foreach (var n in _items)
            {
                var o = new JsonObject
                {
                    ["id"] = n.Id, ["key"] = n.Key, ["kind"] = n.Kind, ["severity"] = n.Severity, ["title"] = n.Title, ["message"] = n.Message,
                };
                if (n.Link != null) o["link"] = n.Link;
                if (n.RequiredPermission != null) o["requiredPermission"] = n.RequiredPermission;
                if (n.Data != null) o["data"] = n.Data.DeepClone();
                o["createdAt"] = n.CreatedAt;
                o["updatedAt"] = n.UpdatedAt;
                o["readBy"] = new JsonArray(n.ReadBy.Select(x => (JsonNode?)x).ToArray());
                o["dismissedBy"] = new JsonArray(n.DismissedBy.Select(x => (JsonNode?)x).ToArray());
                items.Add(o);
            }
            File.WriteAllText(tmp, new JsonObject { ["version"] = FileVersion, ["items"] = items }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning("Saving {File} failed: {Error}", file, ex.Message);
            try
            {
                File.Delete(tmp);
            }
            catch (Exception)
            {
                // nothing written
            }
        }
    }
}
