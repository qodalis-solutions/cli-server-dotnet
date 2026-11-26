using System.Text.Json;
using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Config;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests.Config;

/// <summary>An audit store that keeps what it is given.</summary>
internal sealed class ListAuditStore : IAuditStore
{
    public List<AuditEntry> Entries { get; } = [];
    public Exception? Fail { get; set; }

    public Task LogAsync(AuditEntry entry)
    {
        if (Fail != null) return Task.FromException(Fail);
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<List<AuditEntry>> GetEntriesAsync(AuditQueryOptions? options = null) => Task.FromResult(Entries.ToList());
    public Task ClearAsync() { Entries.Clear(); return Task.CompletedTask; }
}

/// <summary>A read-only provider for tests.</summary>
internal sealed class ReadOnlyProvider(string id, int order = 100, string? requiredPermission = null, Exception? fail = null) : ICliConfigProvider
{
    public string Id => id;
    public string Title => id.ToUpperInvariant();
    public int Order => order;
    public string? RequiredPermission => requiredPermission;

    public Task<IReadOnlyList<CliConfigSection>> GetSectionsAsync(CliConfigContext context, CancellationToken cancellationToken = default) =>
        fail != null
            ? Task.FromException<IReadOnlyList<CliConfigSection>>(fail)
            : Task.FromResult<IReadOnlyList<CliConfigSection>>([new CliConfigSection { Id = "main", Entries = [new CliConfigEntry { Key = "k", Type = "string", Value = id }] }]);
}

/// <summary>A writer whose answers the test decides.</summary>
internal sealed class ScriptedWriter : ICliConfigProvider, ICliConfigWriter
{
    public string Id => "w";
    public string Title => "W";
    public List<CliConfigEntry> Entries { get; init; } = [new() { Key = "a", Type = "number", Mutable = true }];
    public Func<IReadOnlyDictionary<string, JsonNode?>, IReadOnlyDictionary<string, string>> Validate { get; init; } = _ => new Dictionary<string, string>();
    public Queue<Func<CliConfigUpdateResult>> Updates { get; } = new();
    public int UpdateCalls { get; private set; }

    public Task<IReadOnlyList<CliConfigSection>> GetSectionsAsync(CliConfigContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CliConfigSection>>([new CliConfigSection { Id = "s", Version = "1", Entries = Entries }]);

    public Task<IReadOnlyDictionary<string, string>> ValidateAsync(string sectionId, IReadOnlyDictionary<string, JsonNode?> changes,
        CliConfigContext context, CancellationToken cancellationToken = default) => Task.FromResult(Validate(changes));

    public Task<CliConfigUpdateResult> UpdateAsync(string sectionId, IReadOnlyDictionary<string, JsonNode?> changes, string? expectedVersion,
        CliConfigContext context, CancellationToken cancellationToken = default)
    {
        UpdateCalls++;
        return Task.FromResult(Updates.Dequeue()());
    }
}

/// <summary>Config registry and service: registration, ordering, the store, permission filtering, validation, conflicts, secret masking, audit and the config:changed event.</summary>
public class ConfigServiceTests
{
    private static readonly CliConfigContext All = CliConfigContext.For(null, "api");
    private static readonly CliConfigContext Write = As("config:read", "config:write");

    private static CliConfigContext As(params string[] permissions) => CliConfigContext.For(new CliConfigCaller("u1", "ann", permissions), "api");

    private static Dictionary<string, JsonNode?> C(string json) => ConfigStoreContractTests.C(json);

    private static CliConfigUpdateRequest R(string json, string? version = null) => new() { Changes = C(json), Version = version };

    internal static CliStoredConfigProvider App() => new(new CliStoredConfigProviderOptions
    {
        Id = "app",
        Title = "App",
        Sections =
        [
            new()
            {
                Id = "main",
                Settings =
                [
                    new() { Key = "name", Type = "string", Default = "demo", Validation = new() { Required = true, Max = 10 } },
                    new() { Key = "limit", Type = "number", Default = 5, Validation = new() { Min = 1, Max = 10 } },
                    new() { Key = "apiKey", Type = "secret" },
                    new() { Key = "mode", Type = "enum", Default = "a", Validation = new() { Options = ["a", "b"] } },
                    new() { Key = "fixed", Type = "string", Mutable = false, Default = "x" },
                ],
            },
            new() { Id = "info", Settings = [new() { Key = "build", Type = "string", Mutable = false, Default = "1" }] },
        ],
    });

    private sealed record Setup(CliConfigRegistry Registry, CliConfigService Service, ListAuditStore Audits, List<CliEvent> Events);

    private static Setup Make(params ICliConfigProvider[] providers)
    {
        var registry = new CliConfigRegistry();
        foreach (var p in providers.Length == 0 ? [App()] : providers) registry.Add(p);
        registry.Start();
        var audits = new ListAuditStore();
        var bus = new CliEventBus();
        var events = new List<CliEvent>();
        bus.Subscribe("config:*", (e, _) => { events.Add(e); return Task.CompletedTask; });
        var service = new CliConfigService(registry) { AuditStore = () => audits, EventBus = () => bus };
        return new Setup(registry, service, audits, events);
    }

    private static CliConfigEntryView EntryOf(CliConfigProviderView view, string key, int section = 0) =>
        view.Sections[section].Entries.Single(e => e.Key == key);

    private static string Changes(AuditEntry entry) => ((JsonArray)entry.Properties!["changes"]!).ToJsonString();

    // ---------------------------------------------------------------- registry

    [Fact]
    public void Registry_ValidatesIdsAndTitles_RefusesDuplicates()
    {
        var r = new CliConfigRegistry();
        Assert.Equal("invalid config provider: Bad Id", Assert.Throws<ArgumentException>(() => r.Add(new ReadOnlyProvider("Bad Id"))).Message);
        Assert.Contains("needs a title", Assert.Throws<ArgumentException>(() => r.Add(CliCustomConfigProvider.Create("x", ""))).Message);
        r.Add(new ReadOnlyProvider("a"));
        Assert.Equal("config provider already registered: a", Assert.Throws<InvalidOperationException>(() => r.Add(new ReadOnlyProvider("a"))).Message);
        Assert.True(r.Has("a"));
        Assert.Equal("A", r.Find("a")?.Title);
        Assert.Null(r.Find("b"));
    }

    [Fact]
    public void Registry_SortsByOrderThenId()
    {
        var r = new CliConfigRegistry();
        r.Add(new ReadOnlyProvider("zeta"));
        r.Add(new ReadOnlyProvider("alpha"));
        r.Add(new ReadOnlyProvider("first", 0));
        r.Add(new ReadOnlyProvider("last", 1000));
        Assert.Equal(["first", "alpha", "zeta", "last"], r.Providers.Select(p => p.Id));
    }

    [Fact]
    public async Task Registry_TheStore_AttachedToStoredProviders_AlsoThoseAddedLater()
    {
        Assert.Equal("runtime", new CliConfigRegistry().DefaultStore.Source);
        var store = new InMemoryConfigStore();
        await store.WriteAsync("app", "main", C("{\"limit\":9}"));
        await store.WriteAsync("custom", "custom", C("{\"a\":1}"));
        var r = new CliConfigRegistry(store);
        var early = App();
        r.Add(early);
        Assert.False(r.Started);
        r.Start();
        r.Start();
        var late = CliCustomConfigProvider.Create();
        r.Add(late);
        await r.ReadyAsync();
        Assert.Equal(9, CliConfigValues.AsNumber(early.GetValue("main", "limit")));
        Assert.Equal(1, CliConfigValues.AsNumber(late.GetValue("custom", "a")));
    }

    private sealed class DownStore : ICliConfigStore
    {
        public string Source => "database";
        public Task<CliConfigStoredSection> ReadAsync(string provider, string section, CancellationToken cancellationToken = default) =>
            Task.FromException<CliConfigStoredSection>(new InvalidOperationException("down"));
        public Task<CliConfigStoredSection> WriteAsync(string provider, string section, IReadOnlyDictionary<string, JsonNode?> changes,
            long? expectedVersion = null, CancellationToken cancellationToken = default) =>
            Task.FromException<CliConfigStoredSection>(new InvalidOperationException("down"));
    }

    [Fact]
    public async Task Registry_Ready_FailsWhenAProviderFailsToLoad()
    {
        var r = new CliConfigRegistry(new DownStore());
        r.Add(App());
        r.Start();
        Assert.Equal("down", (await Assert.ThrowsAsync<InvalidOperationException>(() => r.ReadyAsync())).Message);
    }

    // ---------------------------------------------------------------- views

    [Fact]
    public void EntryView_DefaultsFilled_SensitiveMasked_IsSet()
    {
        var plain = CliConfigViews.Entry(new CliConfigEntry { Key = "k", Type = "string", Value = "v" }, true);
        Assert.Equal("{\"key\":\"k\",\"label\":\"k\",\"description\":\"\",\"type\":\"string\",\"value\":\"v\",\"default\":null,\"source\":\"default\",\"origin\":\"default\",\"mutable\":false,\"requiresRestart\":false,\"sensitive\":false,\"required\":false}",
            JsonSerializer.Serialize(plain));
        var secret = CliConfigViews.Entry(new CliConfigEntry { Key = "p", Type = "secret", Value = "hunter2", Default = "x", Mutable = true }, true);
        Assert.Equal((null, null, true, true, true), (secret.Value, secret.Default, secret.Sensitive, secret.IsSet, secret.Mutable));
        var url = CliConfigViews.Entry(new CliConfigEntry { Key = "p", Type = "url", Value = "https://u:p@h", Sensitive = true }, true);
        Assert.Equal((null, true), (url.Value, url.IsSet));
        Assert.False(CliConfigViews.Entry(new CliConfigEntry { Key = "p", Type = "secret", IsSet = false }, true).IsSet);
        Assert.False(CliConfigViews.Entry(new CliConfigEntry { Key = "p", Type = "secret", Value = "" }, true).IsSet);
        // Not a writer: nothing is mutable
        Assert.False(CliConfigViews.Entry(new CliConfigEntry { Key = "k", Type = "string", Mutable = true }, false).Mutable);
        var rich = CliConfigViews.Entry(new CliConfigEntry { Key = "k", Type = "string", Editor = "tags", Validation = new() { Options = ["a"] } }, true);
        Assert.Equal("tags", rich.Editor);
        Assert.Equal(["a"], rich.Validation!.Options!);
    }

    [Fact]
    public void SectionView_MutableVersionOnlyWhenMutable_AllowCustomKeysOnlyForWriters()
    {
        var section = new CliConfigSection { Id = "s", Entries = [new() { Key = "k", Type = "string", Mutable = true }], Version = "3" };
        var view = CliConfigViews.Section(section, true);
        Assert.Equal(("s", "s", "", true, "3", false), (view.Id, view.Title, view.Description, view.Mutable, view.Version, view.AllowCustomKeys));
        var ro = CliConfigViews.Section(section, false);
        Assert.Equal((false, null), (ro.Mutable, ro.Version));
        var free = CliConfigViews.Section(new CliConfigSection { Id = "s", AllowCustomKeys = true, Version = "0" }, true);
        Assert.Equal((true, true), (free.Mutable, free.AllowCustomKeys));
        var freeRo = CliConfigViews.Section(new CliConfigSection { Id = "s", AllowCustomKeys = true }, false);
        Assert.Equal((false, false), (freeRo.Mutable, freeRo.AllowCustomKeys));
        Assert.Null(CliConfigViews.Section(new CliConfigSection { Id = "s", Entries = [new() { Key = "k", Type = "string", Mutable = true }] }, true).Version);
        Assert.Contains("\"version\":null", JsonSerializer.Serialize(ro));
    }

    // ---------------------------------------------------------------- reading

    [Fact]
    public async Task List_NeedsConfigRead()
    {
        var s = Make();
        var ex = await Assert.ThrowsAsync<CliConfigException>(() => s.Service.ListAsync(As()));
        Assert.Equal((403, "forbidden: missing permission config:read"), (ex.Status, ex.Message));
        Assert.Equal(["app"], (await s.Service.ListAsync(As("config:read"))).Select(p => p.Id));
    }

    [Fact]
    public async Task List_FiltersByRequiredPermission_AndSorts()
    {
        var s = Make(new ReadOnlyProvider("b"), new ReadOnlyProvider("a", 1), new ReadOnlyProvider("secret-stuff", 0, "ops:root"));
        Assert.Equal(["a", "b"], (await s.Service.ListAsync(As("config:read"))).Select(p => p.Id));
        Assert.Equal(["secret-stuff", "a", "b"], (await s.Service.ListAsync(As("config:read", "ops:root"))).Select(p => p.Id));
        Assert.Equal(["secret-stuff", "a", "b"], (await s.Service.ListAsync(All)).Select(p => p.Id));
    }

    [Fact]
    public async Task ProviderView_WritableOnlyForAWriterAndConfigWrite()
    {
        var s = Make(App(), new ReadOnlyProvider("ro"));
        var list = await s.Service.ListAsync(As("config:read"));
        var app = list[0];
        Assert.Equal(("app", "App", "", 100, false), (app.Id, app.Title, app.Description, app.Order, app.Writable));
        Assert.True((await s.Service.GetAsync("app", Write)).Writable);
        var ro = list[1];
        Assert.False(ro.Writable);
        Assert.Equal((false, null), (ro.Sections[0].Mutable, ro.Sections[0].Version));
        // Entries stay mutable (what the provider allows); writable says what the caller may do
        Assert.True(EntryOf(app, "limit").Mutable);
    }

    [Fact]
    public async Task AFailingProvider_IsLeftOutOfTheList_TheOthersStay()
    {
        var s = Make(new ReadOnlyProvider("broken", fail: new InvalidOperationException("boom")), new ReadOnlyProvider("ok"));
        Assert.Equal(["ok"], (await s.Service.ListAsync(All)).Select(p => p.Id));
        Assert.Equal("boom", (await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service.GetAsync("broken", All))).Message);
    }

    [Fact]
    public async Task Get_403WithoutConfigRead_404Unknown_403WithoutRequiredPermission()
    {
        var s = Make(new ReadOnlyProvider("a"), new ReadOnlyProvider("guarded", 1, "ops:root"));
        Assert.Equal(403, (await Assert.ThrowsAsync<CliConfigException>(() => s.Service.GetAsync("a", As()))).Status);
        var unknown = await Assert.ThrowsAsync<CliConfigException>(() => s.Service.GetAsync("nope", As("config:read")));
        Assert.Equal((404, "unknown config provider: nope"), (unknown.Status, unknown.Message));
        var guarded = await Assert.ThrowsAsync<CliConfigException>(() => s.Service.GetAsync("guarded", As("config:read")));
        Assert.Equal((403, "forbidden: missing permission ops:root"), (guarded.Status, guarded.Message));
        Assert.Equal("guarded", (await s.Service.GetAsync("guarded", As("config:read", "ops:root"))).Id);
    }

    [Fact]
    public async Task NeverReturnsASecret()
    {
        var s = Make();
        await s.Service.UpdateAsync("app", "main", R("{\"apiKey\":\"sk_live_42\"}"), All);
        Assert.DoesNotContain("sk_live_42", JsonSerializer.Serialize(await s.Service.ListAsync(All)));
        var e = EntryOf(await s.Service.GetAsync("app", All), "apiKey");
        Assert.Equal((null, true, true), (e.Value, e.IsSet, e.Sensitive));
    }

    private sealed class LeakyProvider : ICliConfigProvider
    {
        public string Id => "leaky";
        public string Title => "Leaky";
        public Task<IReadOnlyList<CliConfigSection>> GetSectionsAsync(CliConfigContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CliConfigSection>>([new CliConfigSection { Id = "s", Entries = [new() { Key = "token", Type = "secret", Value = "oops" }] }]);
    }

    [Fact]
    public async Task AProviderLeakingASecretValue_IsMaskedAnyway() =>
        Assert.DoesNotContain("oops", JsonSerializer.Serialize(await Make(new LeakyProvider()).Service.GetAsync("leaky", All)));

    // ---------------------------------------------------------------- update

    [Fact]
    public async Task Update_NeedsConfigWrite()
    {
        var ex = await Assert.ThrowsAsync<CliConfigException>(() => Make().Service.UpdateAsync("app", "main", R("{\"limit\":2}"), As("config:read")));
        Assert.Equal((403, "forbidden: missing permission config:write"), (ex.Status, ex.Message));
    }

    [Fact]
    public async Task Update_404UnknownProviderOrSection_403GuardedProvider()
    {
        var guarded = new CliStoredConfigProvider(new CliStoredConfigProviderOptions
        {
            Id = "g", Title = "G", RequiredPermission = "ops:root",
            Sections = [new() { Id = "s", Settings = [new() { Key = "k", Type = "string" }] }],
        });
        var s = Make(App(), guarded);
        Assert.Equal(404, (await Assert.ThrowsAsync<CliConfigException>(() => s.Service.UpdateAsync("nope", "main", R("{\"a\":1}"), Write))).Status);
        var section = await Assert.ThrowsAsync<CliConfigException>(() => s.Service.UpdateAsync("app", "nope", R("{\"a\":1}"), Write));
        Assert.Equal((404, "unknown config section: app.nope"), (section.Status, section.Message));
        Assert.Equal(403, (await Assert.ThrowsAsync<CliConfigException>(() => s.Service.UpdateAsync("g", "s", R("{\"k\":\"v\"}"), Write))).Status);
        Assert.Equal("1", (await s.Service.UpdateAsync("g", "s", R("{\"k\":\"v\"}"), As("config:write", "config:read", "ops:root"))).Version);
    }

    [Fact]
    public async Task Update_400ForAReadOnlyProviderOrSection()
    {
        var s = Make(App(), new ReadOnlyProvider("ro"));
        var provider = await Assert.ThrowsAsync<CliConfigException>(() => s.Service.UpdateAsync("ro", "main", R("{\"k\":\"x\"}"), Write));
        Assert.Equal((400, "config provider ro is read-only"), (provider.Status, provider.Message));
        var section = await Assert.ThrowsAsync<CliConfigException>(() => s.Service.UpdateAsync("app", "info", R("{\"build\":\"2\"}"), Write));
        Assert.Equal((400, "config section app.info is read-only"), (section.Status, section.Message));
    }

    [Fact]
    public async Task Update_400ForMissingOrEmptyChanges()
    {
        var s = Make();
        foreach (var request in new[] { new CliConfigUpdateRequest(), R("{}") })
        {
            var ex = await Assert.ThrowsAsync<CliConfigException>(() => s.Service.UpdateAsync("app", "main", request, Write));
            Assert.Equal((400, "changes must be a non-empty object"), (ex.Status, ex.Message));
        }
    }

    [Fact]
    public async Task Update_400WithPerKeyErrors()
    {
        var s = Make();
        var ex = await Assert.ThrowsAsync<CliConfigException>(() =>
            s.Service.UpdateAsync("app", "main", R("{\"nope\":1,\"fixed\":\"y\",\"limit\":\"ten\",\"name\":\"\",\"mode\":\"c\",\"apiKey\":5}"), Write));
        Assert.Equal((400, "validation failed"), (ex.Status, ex.Message));
        Assert.Equal(new Dictionary<string, string>
        {
            ["nope"] = "unknown setting",
            ["fixed"] = "read-only",
            ["limit"] = "must be a number",
            ["name"] = "is required",
            ["mode"] = "must be one of: a, b",
            ["apiKey"] = "must be a string",
        }, ex.Errors);
        var range = await Assert.ThrowsAsync<CliConfigException>(() => s.Service.UpdateAsync("app", "main", R("{\"limit\":11,\"name\":\"much too long\"}"), Write));
        Assert.Equal(new Dictionary<string, string> { ["limit"] = "must be at most 10", ["name"] = "must be at most 10 characters" }, range.Errors);
        // Nothing changed
        Assert.Equal("0", (await s.Service.GetAsync("app", All)).Sections[0].Version);
    }

    [Fact]
    public async Task TheWritersOwnValidateErrors_AreReturnedToo()
    {
        var writer = new ScriptedWriter
        {
            Entries = [new() { Key = "a", Type = "number", Mutable = true }, new() { Key = "b", Type = "number", Mutable = true }],
            Validate = c => CliConfigValues.AsNumber(c["a"]) > CliConfigValues.AsNumber(c["b"])
                ? new Dictionary<string, string> { ["a"] = "must not exceed b" }
                : new Dictionary<string, string>(),
        };
        writer.Updates.Enqueue(() => new CliConfigUpdateResult { Applied = ["a", "b"], Version = "2" });
        var s = Make(writer);
        var ex = await Assert.ThrowsAsync<CliConfigException>(() => s.Service.UpdateAsync("w", "s", R("{\"a\":5,\"b\":1}"), Write));
        Assert.Equal((400, "must not exceed b"), (ex.Status, ex.Errors!["a"]));
        Assert.Equal(0, writer.UpdateCalls);
        Assert.Equal("2", (await s.Service.UpdateAsync("w", "s", R("{\"a\":1,\"b\":5}"), Write)).Version);
    }

    [Fact]
    public async Task Update_409ForAStaleVersion_WithTheCurrentOne_NothingChanges()
    {
        var s = Make();
        await s.Service.UpdateAsync("app", "main", R("{\"limit\":2}", "0"), Write);
        var ex = await Assert.ThrowsAsync<CliConfigException>(() => s.Service.UpdateAsync("app", "main", R("{\"limit\":3}", "0"), Write));
        Assert.Equal((409, "version conflict", "1"), (ex.Status, ex.Message, ex.Version));
        Assert.Equal("{\"error\":\"version conflict\",\"version\":\"1\"}", ex.ToJson().ToJsonString());
        Assert.Single(s.Audits.Entries);
        Assert.Single(s.Events);
        Assert.Equal(2, CliConfigValues.AsNumber(EntryOf(await s.Service.GetAsync("app", All), "limit").Value));
    }

    [Fact]
    public async Task Update_409WhenTheWriterReportsAConflict_400ForItsLateErrors()
    {
        var writer = new ScriptedWriter();
        writer.Updates.Enqueue(() => throw new CliConfigConflictException("7"));
        writer.Updates.Enqueue(() => throw new CliConfigValidationException(new Dictionary<string, string> { ["a"] = "taken" }));
        writer.Updates.Enqueue(() => throw new InvalidOperationException("disk full"));
        var s = Make(writer);
        var conflict = await Assert.ThrowsAsync<CliConfigException>(() => s.Service.UpdateAsync("w", "s", R("{\"a\":1}", "1"), Write));
        Assert.Equal((409, "7"), (conflict.Status, conflict.Version));
        var late = await Assert.ThrowsAsync<CliConfigException>(() => s.Service.UpdateAsync("w", "s", R("{\"a\":1}"), Write));
        Assert.Equal((400, "taken"), (late.Status, late.Errors!["a"]));
        Assert.Equal("disk full", (await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service.UpdateAsync("w", "s", R("{\"a\":1}"), Write))).Message);
    }

    [Fact]
    public async Task Update_AppliesWithoutAVersion_AnswersTheSectionAppliedKeysAndTheNewVersion()
    {
        var result = await Make().Service.UpdateAsync("app", "main", R("{\"limit\":7,\"mode\":\"b\"}"), Write);
        Assert.Equal("app", result.Provider);
        Assert.Equal(["limit", "mode"], result.Applied);
        Assert.False(result.RequiresRestart);
        Assert.Equal(("1", "1"), (result.Version, result.Section.Version));
        var limit = result.Section.Entries.Single(e => e.Key == "limit");
        Assert.Equal((7.0, "runtime"), (CliConfigValues.AsNumber(limit.Value), limit.Source));
    }

    [Fact]
    public async Task Update_NullResetsToTheDefault()
    {
        var s = Make();
        await s.Service.UpdateAsync("app", "main", R("{\"limit\":7}"), Write);
        var result = await s.Service.UpdateAsync("app", "main", R("{\"limit\":null}"), Write);
        var limit = result.Section.Entries.Single(e => e.Key == "limit");
        Assert.Equal((5.0, "default"), (CliConfigValues.AsNumber(limit.Value), limit.Source));
    }

    [Fact]
    public async Task CustomKeys_AddedTypedByTheirValue_RemovedWithNull()
    {
        var s = Make(CliCustomConfigProvider.Create());
        var added = await s.Service.UpdateAsync("custom", "custom", R("{\"motd\":\"hi\",\"feature.x\":true}"), Write);
        Assert.Equal(["motd string \"hi\"", "feature.x boolean true"], added.Section.Entries.Select(e => $"{e.Key} {e.Type} {e.Value!.ToJsonString()}"));
        var bad = await Assert.ThrowsAsync<CliConfigException>(() => s.Service.UpdateAsync("custom", "custom", R("{\"bad key\":1}"), Write));
        Assert.Equal((400, "invalid key: use letters, digits, _, - and ."), (bad.Status, bad.Errors!["bad key"]));
        var removed = await s.Service.UpdateAsync("custom", "custom", R("{\"motd\":null}"), Write);
        Assert.Equal(["feature.x"], removed.Section.Entries.Select(e => e.Key));
    }

    // ---------------------------------------------------------------- audit and event

    [Fact]
    public async Task Audits_ConfigUpdate_WithOldAndNewValues_TheCallerAndTheTarget()
    {
        var s = Make();
        await s.Service.UpdateAsync("app", "main", R("{\"limit\":8,\"mode\":\"b\"}", "0"), Write);
        var entry = Assert.Single(s.Audits.Entries);
        Assert.Equal(("config.update", "ann", "u1", "ann", "u1", "config:app.main", "app.main: limit, mode"),
            (entry.Event, entry.Username, entry.UserId, entry.Actor, entry.ActorId, entry.Target, entry.Details));
        Assert.Equal("{\"provider\":\"app\",\"section\":\"main\",\"version\":\"1\",\"store\":\"memory\",\"via\":\"api\",\"changes\":[{\"key\":\"limit\",\"old\":5,\"new\":8},{\"key\":\"mode\",\"old\":\"a\",\"new\":\"b\"}]}",
            JsonSerializer.Serialize(entry.Properties));
        Assert.True(entry.Timestamp > 0);
    }

    [Fact]
    public async Task SecretsAreMaskedInTheAudit_SetReplacedCleared()
    {
        var s = Make();
        await s.Service.UpdateAsync("app", "main", R("{\"apiKey\":\"first-secret\"}"), Write);
        await s.Service.UpdateAsync("app", "main", R("{\"apiKey\":\"second-secret\"}"), Write);
        await s.Service.UpdateAsync("app", "main", R("{\"apiKey\":null}"), Write);
        Assert.Equal(
            ["[{\"key\":\"apiKey\",\"old\":null,\"new\":\"***\"}]", "[{\"key\":\"apiKey\",\"old\":\"***\",\"new\":\"***\"}]", "[{\"key\":\"apiKey\",\"old\":\"***\",\"new\":null}]"],
            s.Audits.Entries.Select(Changes));
        Assert.DoesNotContain("secret\"", JsonSerializer.Serialize(s.Audits.Entries).Replace("\"***\"", ""));
        Assert.DoesNotContain("first-secret", JsonSerializer.Serialize(s.Audits.Entries));
    }

    [Fact]
    public async Task AnonymousCallers_AreAuditedAsAnonymous_RemovedCustomKeysAsNull()
    {
        var s = Make(CliCustomConfigProvider.Create());
        await s.Service.UpdateAsync("custom", "custom", R("{\"a\":1}"), All);
        await s.Service.UpdateAsync("custom", "custom", R("{\"a\":null}"), All);
        Assert.Equal(("anonymous", "anonymous", null), (s.Audits.Entries[0].Username, s.Audits.Entries[0].Actor, s.Audits.Entries[0].UserId));
        Assert.Equal("[{\"key\":\"a\",\"old\":null,\"new\":1}]", Changes(s.Audits.Entries[0]));
        Assert.Equal("[{\"key\":\"a\",\"old\":1,\"new\":null}]", Changes(s.Audits.Entries[1]));
    }

    [Fact]
    public async Task AFailingAuditStore_DoesNotFailTheChange_NoStoreNoAudit()
    {
        var registry = new CliConfigRegistry();
        registry.Add(App());
        var failing = new CliConfigService(registry) { AuditStore = () => new ListAuditStore { Fail = new InvalidOperationException("audit down") } };
        Assert.Equal("1", (await failing.UpdateAsync("app", "main", R("{\"limit\":2}"), All)).Version);
        var bare = new CliConfigService(registry);
        Assert.Equal("2", (await bare.UpdateAsync("app", "main", R("{\"limit\":3}"), All)).Version);
    }

    [Fact]
    public async Task PublishesConfigChanged_ForConfigReadHolders_WithoutValues()
    {
        var s = Make();
        await s.Service.UpdateAsync("app", "main", R("{\"apiKey\":\"shh\",\"limit\":2}"), Write);
        var e = Assert.Single(s.Events);
        Assert.Equal("config:changed", e.Type);
        Assert.Equal(CliEventAudience.WithPermission("config:read"), e.Audience);
        Assert.Equal("{\"provider\":\"app\",\"section\":\"main\",\"keys\":[\"apiKey\",\"limit\"],\"version\":\"1\",\"requiresRestart\":false,\"store\":\"memory\",\"user\":\"ann\"}",
            JsonSerializer.Serialize(e.Data));
        Assert.DoesNotContain("shh", JsonSerializer.Serialize(e.Data));
    }

    [Fact]
    public async Task NoAuditAndNoEvent_ForRefusedChanges()
    {
        var s = Make();
        await Assert.ThrowsAsync<CliConfigException>(() => s.Service.UpdateAsync("app", "main", R("{\"limit\":99}"), Write));
        await Assert.ThrowsAsync<CliConfigException>(() => s.Service.UpdateAsync("app", "main", R("{\"limit\":2}"), As("config:read")));
        Assert.Empty(s.Audits.Entries);
        Assert.Empty(s.Events);
    }
}
