using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Abstractions.Notifications;
using Qodalis.Cli.Notifications;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// The notification service on its own: dedupe by key, resolve and ResolveMissing, permission filtering, per-user read
/// and dismiss state, clear for everyone, order and filters, persistence (read state included), retention, damaged
/// files, invalid input and the <c>notification:changed</c> events with their audience.
/// </summary>
public sealed class NotificationServiceTests : IDisposable
{
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qcli-notif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    internal static CliNotificationViewer Viewer(string userId, params string[] permissions) =>
        new(userId, p => CliPermissionMatcher.HasPermission(permissions, p));

    private static readonly CliNotificationViewer Alice = Viewer("alice", "admin:access");
    private static readonly CliNotificationViewer Bob = Viewer("bob");

    internal static CliNotificationInput Input(string key, string severity = "info", string? title = null, string message = "Details",
        string? link = null, string? permission = null, JsonObject? data = null, string kind = "test") => new()
    {
        Key = key, Kind = kind, Severity = severity, Title = title ?? $"Title {key}", Message = message, Link = link, RequiredPermission = permission,
        Data = data,
    };

    private static (CliEventBus Bus, List<CliEvent> Events) Bus()
    {
        var bus = new CliEventBus();
        var events = new List<CliEvent>();
        bus.Subscribe(CliNotificationConstants.ChangedEvent, (e, _) =>
        {
            lock (events) events.Add(e);
            return Task.CompletedTask;
        });
        return (bus, events);
    }

    private static ManualTimeProvider Clock(string iso)
    {
        var clock = new ManualTimeProvider();
        clock.SetUtcNow(DateTimeOffset.Parse(iso));
        return clock;
    }

    [Fact]
    public void Dedupe_SameKeyUpdates_IdenticalContentChangesNothing_ChangedContentIsUnreadAgain()
    {
        var (bus, events) = Bus();
        var clock = Clock("2026-11-26T10:00:00Z");
        var s = new CliNotificationService(eventBus: () => bus, time: clock);
        var first = s.Notify(Input("a"));
        Assert.Matches("^n_[0-9a-f]{16}$", first.Id);
        Assert.Equal(("a", "test", "info", "Title a", "2026-11-26T10:00:00.000Z"), (first.Key, first.Kind, first.Severity, first.Title, first.CreatedAt));
        Assert.Null(first.Read);

        s.MarkRead(Alice, first.Id);
        // identical: no update, still read, no event
        clock.Advance(TimeSpan.FromMinutes(5));
        s.Notify(Input("a"));
        var listed = Assert.Single(s.List(Alice).Items);
        Assert.True(listed.Read);
        Assert.Equal("2026-11-26T10:00:00.000Z", listed.UpdatedAt);

        clock.SetUtcNow(DateTimeOffset.Parse("2026-11-26T11:00:00Z"));
        var updated = s.Notify(Input("a", severity: "warning", title: "Changed"));
        Assert.Equal(first.Id, updated.Id);
        Assert.Equal("2026-11-26T10:00:00.000Z", updated.CreatedAt);
        Assert.Equal("2026-11-26T11:00:00.000Z", updated.UpdatedAt);
        var list = s.List(Alice);
        Assert.Equal(1, list.UnreadCount);
        Assert.Equal(("Changed", "warning", false), (list.Items[0].Title, list.Items[0].Severity, list.Items[0].Read));
        Assert.Equal(["created", "read", "updated"], events.Select(e => (string)e.Data["action"]!));
    }

    [Fact]
    public void Resolve_RemovesTheKey_ResolveMissingKeepsOnlyTheListedKeysOfAPrefix()
    {
        var s = new CliNotificationService();
        s.Notify(Input("update:a"));
        s.Notify(Input("update:b"));
        s.Notify(Input("other"));
        Assert.True(s.Resolve("update:a"));
        Assert.False(s.Resolve("update:a"));
        s.Notify(Input("update:c"));
        Assert.Equal(1, s.ResolveMissing("update:", ["update:c"]));
        Assert.Equal(["other", "update:c"], s.List(Alice).Items.Select(n => n.Key).Order());
        Assert.Null(s.Get("update:b"));
    }

    [Fact]
    public void FiltersByRequiredPermission_HiddenOnesCannotBeReadDismissedOrCleared()
    {
        var s = new CliNotificationService();
        s.Notify(Input("public"));
        s.Notify(Input("admin", permission: "admin:access"));
        Assert.Equal(["admin", "public"], s.List(Alice).Items.Select(n => n.Key).Order());
        Assert.Equal(["public"], s.List(Bob).Items.Select(n => n.Key));
        Assert.Equal(1, s.UnreadCount(Bob));
        // a wildcard grants it
        Assert.Equal(2, s.List(Viewer("root", "*")).Items.Count);
        var hidden = s.Get("admin")!;
        Assert.Null(s.MarkRead(Bob, hidden.Id));
        Assert.Null(s.Find(Bob, hidden.Id));
        Assert.False(s.Dismiss(Bob, hidden.Id));
        Assert.False(s.Clear(Bob, hidden.Id));
    }

    [Fact]
    public void ReadStateIsPerUser_ReadAllAndDismissAffectOnlyThatUser_ClearRemovesForEveryone()
    {
        var s = new CliNotificationService();
        var a = s.Notify(Input("a"));
        s.Notify(Input("b"));
        s.MarkRead(Alice, a.Id);
        Assert.Equal(1, s.UnreadCount(Alice));
        Assert.Equal(2, s.UnreadCount(Bob));
        Assert.Equal(["b"], s.List(Alice, unread: true).Items.Select(n => n.Key));
        Assert.Equal(2, s.MarkAllRead(Bob));
        Assert.Equal(0, s.MarkAllRead(Bob));
        Assert.Equal(0, s.UnreadCount(Bob));
        Assert.Equal(1, s.UnreadCount(Alice));

        Assert.True(s.Dismiss(Alice, a.Id));
        Assert.Equal(["b"], s.List(Alice).Items.Select(n => n.Key));
        Assert.Equal(2, s.List(Bob).Items.Count);
        Assert.True(s.Clear(Bob, a.Id));
        Assert.Equal(["b"], s.List(Bob).Items.Select(n => n.Key));
        Assert.Null(s.Get("a"));
    }

    [Fact]
    public void ADismissedNotification_ComesBackWhenItsContentChanges()
    {
        var s = new CliNotificationService();
        var a = s.Notify(Input("a"));
        s.Dismiss(Alice, a.Id);
        Assert.Empty(s.List(Alice).Items);
        s.Notify(Input("a", message: "New details"));
        var back = Assert.Single(s.List(Alice).Items);
        Assert.Equal(("a", false, "New details"), (back.Key, back.Read, back.Message));
    }

    [Fact]
    public void ListsNewestFirst_FiltersBySeverityAndLimit_UnreadCountCountsEverything()
    {
        var clock = Clock("2026-11-26T10:00:00Z");
        var s = new CliNotificationService(time: clock);
        foreach (var (key, severity) in new[] { ("a", "info"), ("b", "error"), ("c", "info") })
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            s.Notify(Input(key, severity));
        }
        Assert.Equal(["c", "b", "a"], s.List(Alice).Items.Select(n => n.Key));
        Assert.Equal(["b"], s.List(Alice, severity: "error").Items.Select(n => n.Key));
        Assert.Equal(["c", "b"], s.List(Alice, limit: 2).Items.Select(n => n.Key));
        Assert.Equal(3, s.List(Alice, limit: 2).UnreadCount);
    }

    [Fact]
    public void PersistsAcrossARestart_ReadAndDismissedStateIncluded()
    {
        var file = Path.Combine(TempDir(), "data", "notifications.json");
        var s1 = new CliNotificationService(new CliNotificationsOptions { File = file });
        var a = s1.Notify(Input("a", permission: "admin:access", link: "/plugins", data: new JsonObject { ["n"] = 1 }));
        var b = s1.Notify(Input("b"));
        s1.MarkRead(Alice, a.Id);
        s1.Dismiss(Bob, b.Id);
        var saved = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal(1, saved["version"]!.GetValue<int>());
        Assert.Equal(2, saved["items"]!.AsArray().Count);
        Assert.Equal(["alice"], saved["items"]![0]!["readBy"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp"));

        var s2 = new CliNotificationService(new CliNotificationsOptions { File = file });
        var again = s2.List(Alice).Items.Single(n => n.Key == "a");
        Assert.Equal((a.Id, true, "/plugins", 1), (again.Id, again.Read, again.Link, again.Data!["n"]!.GetValue<int>()));
        Assert.Empty(s2.List(Bob).Items);
        Assert.True(s2.Resolve("a"));
        Assert.Null(new CliNotificationService(new CliNotificationsOptions { File = file }).Get("a"));
    }

    [Fact]
    public void Configure_LoadsTheFile_WhatWasPublishedBeforeWinsAndIsSaved()
    {
        var file = Path.Combine(TempDir(), "notifications.json");
        var old = new CliNotificationService(new CliNotificationsOptions { File = file });
        old.Notify(Input("kept"));
        old.Notify(Input("both", title: "From the file"));

        var s = new CliNotificationService();
        s.Notify(Input("both", title: "Published before"));
        s.Configure(new CliNotificationsOptions { File = file });
        Assert.Equal("Published before", s.Get("both")!.Title);
        Assert.NotNull(s.Get("kept"));
        Assert.Equal("Published before", new CliNotificationService(new CliNotificationsOptions { File = file }).Get("both")!.Title);

        // memory only
        var memory = new CliNotificationService(new CliNotificationsOptions { File = Path.Combine(TempDir(), "x.json"), Persist = false });
        memory.Notify(Input("gone"));
        Assert.Null(memory.FilePath);
    }

    [Fact]
    public void CapsTheList_AndDropsNotifications30DaysAfterTheirLastChange()
    {
        var clock = Clock("2026-11-01T00:00:00Z");
        var s = new CliNotificationService(new CliNotificationsOptions { MaxItems = 3 }, time: clock);
        foreach (var key in new[] { "a", "b", "c", "d" })
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            s.Notify(Input(key));
        }
        Assert.Equal(["d", "c", "b"], s.List(Alice).Items.Select(n => n.Key));
        clock.SetUtcNow(DateTimeOffset.Parse("2026-12-01T00:02:30Z"));
        Assert.Equal(["d", "c"], s.List(Alice).Items.Select(n => n.Key));
        clock.SetUtcNow(DateTimeOffset.Parse("2026-12-02T00:00:00Z"));
        Assert.Empty(s.List(Alice).Items);
        Assert.Throws<ArgumentException>(() => new CliNotificationService(new CliNotificationsOptions { MaxItems = 0 }));
        Assert.Throws<ArgumentException>(() => new CliNotificationService(new CliNotificationsOptions { MaxAgeDays = 0 }));
    }

    [Fact]
    public void IgnoresADamagedFile_AndDamagedEntries()
    {
        var dir = TempDir();
        var bad = Path.Combine(dir, "bad.json");
        File.WriteAllText(bad, "{ not json");
        Assert.Empty(new CliNotificationService(new CliNotificationsOptions { File = bad }).List(Alice).Items);
        var partial = Path.Combine(dir, "partial.json");
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        File.WriteAllText(partial, new JsonObject
        {
            ["version"] = 1,
            ["items"] = new JsonArray(
                new JsonObject { ["id"] = "n_1", ["key"] = "ok", ["kind"] = "k", ["severity"] = "info", ["title"] = "T", ["message"] = "", ["createdAt"] = now, ["updatedAt"] = now },
                new JsonObject { ["key"] = "broken" },
                new JsonObject { ["id"] = "n_2", ["key"] = "bad severity", ["kind"] = "k", ["severity"] = "fatal", ["title"] = "T", ["createdAt"] = now, ["updatedAt"] = now },
                "not an object"),
        }.ToJsonString());
        Assert.Equal(["ok"], new CliNotificationService(new CliNotificationsOptions { File = partial }).List(Alice).Items.Select(n => n.Key));
    }

    [Fact]
    public void RefusesInvalidInput_NamingTheField()
    {
        var s = new CliNotificationService();
        Assert.Contains("key", Assert.Throws<ArgumentException>(() => s.Notify(Input("bad key with spaces"))).Message);
        Assert.Contains("kind", Assert.Throws<ArgumentException>(() => s.Notify(Input("k", kind: "Bad Kind"))).Message);
        Assert.Contains("severity", Assert.Throws<ArgumentException>(() => s.Notify(Input("k", severity: "fatal"))).Message);
        Assert.Contains("title", Assert.Throws<ArgumentException>(() => s.Notify(Input("k", title: "  "))).Message);
        Assert.Contains("title", Assert.Throws<ArgumentException>(() => s.Notify(Input("k", title: new string('x', 201)))).Message);
        Assert.Contains("message", Assert.Throws<ArgumentException>(() => s.Notify(Input("k", message: new string('x', 2001)))).Message);
        Assert.Contains("link", Assert.Throws<ArgumentException>(() => s.Notify(Input("k", link: "javascript:alert(1)"))).Message);
        Assert.Contains("link", Assert.Throws<ArgumentException>(() => s.Notify(Input("k", link: "//evil.test"))).Message);
        Assert.Contains("requiredPermission", Assert.Throws<ArgumentException>(() => s.Notify(Input("k", permission: "admin"))).Message);
        Assert.Contains("8 KB", Assert.Throws<ArgumentException>(() => s.Notify(Input("k", data: new JsonObject { ["big"] = new string('x', 9000) }))).Message);
        Assert.Equal("https://example.test/x", s.Notify(Input("k", link: "https://example.test/x")).Link);
        Assert.Equal("/", s.Notify(Input("root", link: "/")).Link);
        // trimmed
        Assert.Equal(("Trimmed", "body"), (s.Notify(Input("t", title: "  Trimmed ", message: " body ")).Title, s.Get("t")!.Message));
        Assert.Empty(s.List(Alice).Items.Where(n => n.Key.Contains(' ')));
    }

    [Fact]
    public void PublishesNotificationChanged_WithTheItem_ToThePermissionOrTheUser()
    {
        var (bus, events) = Bus();
        var s = new CliNotificationService(eventBus: () => bus);
        var n = s.Notify(Input("a", permission: "admin:access"));
        s.MarkRead(Alice, n.Id);
        s.MarkAllRead(Alice);
        s.Dismiss(Alice, n.Id);
        s.Resolve("a");
        var open = s.Notify(Input("open"));
        s.MarkAllRead(Bob);
        s.Clear(Bob, open.Id);

        Assert.Equal(
            ["created admin:access -", "read - alice", "dismissed - alice", "resolved admin:access -", "created - -", "read-all - bob", "cleared - -"],
            events.Select(e => $"{e.Data["action"]} {e.Audience.Permission ?? "-"} {e.Audience.UserId ?? "-"}"));
        var item = Assert.IsType<CliNotification>(events[0].Data["item"]);
        Assert.Equal((n.Id, "a", "Title a", (bool?)null), (item.Id, item.Key, item.Title, item.Read));
        Assert.Equal(n.Id, events[0].Data["id"]);
        Assert.False(events[3].Data.ContainsKey("item"));
        Assert.Equal("a", events[3].Data["key"]);
        Assert.False(events[5].Data.ContainsKey("id"));
        Assert.False(events[6].Data.ContainsKey("item"));
    }

    [Fact]
    public void Personalize_SetsTheReadersUnreadCountAndReadFlag()
    {
        var (bus, events) = Bus();
        var s = new CliNotificationService(eventBus: () => bus);
        var a = s.Notify(Input("a"));
        s.Notify(Input("b"));
        s.MarkRead(Alice, a.Id);
        var created = events[0];
        var forAlice = s.Personalize(created, Alice);
        var forBob = s.Personalize(created, Bob);
        Assert.Equal((1, true), ((int)forAlice.Data["unreadCount"]!, ((CliNotification)forAlice.Data["item"]!).Read));
        Assert.Equal((2, false), ((int)forBob.Data["unreadCount"]!, ((CliNotification)forBob.Data["item"]!).Read));
        // the bus event itself is not changed
        Assert.False(created.Data.ContainsKey("unreadCount"));
        Assert.Null(((CliNotification)created.Data["item"]!).Read);
    }
}
