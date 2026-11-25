using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Config;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.Config.EfCore;
using Qodalis.Cli.Plugin.Jobs;
using Qodalis.Cli.Plugin.Portal;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests.Config;

/// <summary>A store recording its writes, described as <c>name</c> (a fresh instance per DI resolution when made by the container).</summary>
internal class NamedStore : ICliConfigStore
{
    private readonly InMemoryConfigStore _inner = new();

    public NamedStore() : this("named")
    {
    }

    public NamedStore(string name, string kind = CliConfigStoreKinds.Custom)
    {
        Name = name;
        Kind = kind;
    }

    public string Name { get; }
    public string Kind { get; }
    public List<string> Writes { get; } = [];
    public Func<Task>? Ready { get; set; }

    public string Source => CliConfigSources.Runtime;

    public CliConfigStoreDescription? Describe() =>
        new() { Kind = Kind, Title = Name, Detail = $"{Name}-detail", Persistent = Kind != CliConfigStoreKinds.Memory };

    public Task ReadyAsync(CancellationToken cancellationToken = default) => Ready?.Invoke() ?? Task.CompletedTask;

    public Task<CliConfigStoredSection> ReadAsync(string provider, string section, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(provider, section, cancellationToken);

    public Task<CliConfigStoredSection> WriteAsync(string provider, string section, IReadOnlyDictionary<string, JsonNode?> changes,
        long? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        lock (Writes) Writes.Add($"{provider}.{section}");
        return _inner.WriteAsync(provider, section, changes, expectedVersion, cancellationToken);
    }
}

/// <summary>A store type the container builds (for <c>SetConfigStore&lt;TStore&gt;(id)</c>).</summary>
internal sealed class ContainerStore() : NamedStore("container");

/// <summary>
/// A config store per provider: the resolution order (the host's store for the id, the provider's own, the default store,
/// memory), providers in different stores and one store shared by two, versions and conflicts per store, ready over every
/// store, the plugins' own stores, and what GET /config, MCP, config list, the audit entry and the config:changed event say
/// about each provider's store.
/// </summary>
public sealed class ConfigMultiStoreTests
{
    private static readonly CliConfigContext All = CliConfigContext.For(null, "api");

    internal static CliStoredConfigProvider Provider(string id, ICliConfigStore? store = null, Action<JsonNode?>? apply = null) =>
        new(new CliStoredConfigProviderOptions
        {
            Id = id,
            Title = char.ToUpperInvariant(id[0]) + id[1..],
            Store = store,
            Sections =
            [
                new()
                {
                    Id = "main",
                    Settings =
                    [
                        new() { Key = "limit", Type = "number", Default = 5, Apply = apply },
                        new() { Key = "name", Type = "string", Default = "x" },
                    ],
                },
            ],
        });

    private static Dictionary<string, JsonNode?> C(string json) => ConfigStoreContractTests.C(json);

    private static CliConfigUpdateRequest R(string json, string? version = null) => new() { Changes = C(json), Version = version };

    // ---------------------------------------------------------------- resolution

    [Fact]
    public void TheHostsStoreForTheId_ThenTheProvidersOwn_ThenTheDefaultStore()
    {
        var defaultStore = new NamedStore("default");
        var hosts = new NamedStore("hosts");
        var own = new NamedStore("own");
        var registry = new CliConfigRegistry(defaultStore);
        registry.SetStore(hosts, "a");
        var a = Provider("a", own);
        var b = Provider("b", own);
        var c = Provider("c");
        var readOnly = new ReadOnlyProvider("r");
        foreach (var p in new ICliConfigProvider[] { a, b, c, readOnly }) registry.Add(p);
        registry.Start();
        Assert.Same(hosts, registry.StoreOf(a));
        Assert.Same(own, registry.StoreOf("b"));
        Assert.Same(defaultStore, registry.StoreOf(c));
        Assert.Null(registry.StoreOf(readOnly));
        Assert.Null(registry.StoreOf("missing"));
        Assert.Same(hosts, a.ConfigStore);
        Assert.Same(own, a.OwnStore);
        Assert.Same(defaultStore, registry.DefaultStore);
        Assert.Equal([defaultStore, hosts, own], registry.Stores);
        Assert.Same(hosts, registry.ProviderStores["a"]);
    }

    [Fact]
    public async Task AProviderWithNoStoreAnywhere_KeepsItsChangesInMemory()
    {
        var registry = new CliConfigRegistry();
        var p = Provider("p");
        Assert.Null(p.ConfigStore);
        registry.Add(p);
        registry.Start();
        await registry.ReadyAsync();
        Assert.Equal(CliConfigStoreKinds.Memory, CliConfigStoreHelpers.Describe(registry.StoreOf(p)!).Kind);
        var service = new CliConfigService(registry);
        await service.UpdateAsync("p", "main", R("{\"limit\":7}"), All);
        Assert.Equal(7, CliConfigValues.AsNumber(p.GetValue("main", "limit")));
        Assert.Equal(1, (await registry.DefaultStore.ReadAsync("p", "main")).Version);
    }

    [Fact]
    public void TheProvidersOwnStore_CanBeSetAfterConstruction_NotOnceRegistered()
    {
        var store = new NamedStore("late");
        var p = Provider("p").SetStore(store);
        Assert.Same(store, p.OwnStore);
        var registry = new CliConfigRegistry();
        registry.Add(p);
        registry.Start();
        Assert.Same(store, registry.StoreOf(p));
        var ex = Assert.Throws<InvalidOperationException>(() => p.SetStore(new NamedStore("too-late")));
        Assert.Equal("the config store of p must be set before the provider is registered", ex.Message);
    }

    [Fact]
    public async Task AStoreForAnId_MayBeSetAfterTheServerStarted_UntilThatProviderIsRegistered()
    {
        var registry = new CliConfigRegistry();
        registry.Add(Provider("early"));
        registry.Start();
        var late = new NamedStore("late");
        registry.SetStore(late, "later");
        var p = Provider("later");
        registry.Add(p);
        await registry.ReadyAsync();
        Assert.Same(late, registry.StoreOf(p));
        Assert.Equal("the config store of later must be set before the provider is registered",
            Assert.Throws<InvalidOperationException>(() => registry.SetStore(new NamedStore("x"), "later")).Message);
        Assert.Equal("the config store of early must be set before the provider is registered",
            Assert.Throws<InvalidOperationException>(() => registry.SetStore(new NamedStore("x"), "early")).Message);
        Assert.Equal("the default config store must be set before the server starts",
            Assert.Throws<InvalidOperationException>(() => registry.SetStore(new NamedStore("x"))).Message);
    }

    [Fact]
    public void RefusesANullStore_AndAnInvalidProviderId()
    {
        var registry = new CliConfigRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.SetStore(null!, "a"));
        Assert.Equal("invalid config provider id: Bad Id", Assert.Throws<ArgumentException>(() => registry.SetStore(new NamedStore("x"), "Bad Id")).Message);
        Assert.Throws<ArgumentException>(() => new CliConfigStoreRegistration("Bad Id", _ => new NamedStore("x")));
    }

    [Fact]
    public async Task StoredValues_AreAppliedFromEachProvidersOwnStore_WhenTheServerStarts()
    {
        var own = new NamedStore("own");
        await own.WriteAsync("a", "main", C("{\"limit\":8}"));
        var defaultStore = new NamedStore("default");
        await defaultStore.WriteAsync("a", "main", C("{\"limit\":1}"));
        await defaultStore.WriteAsync("b", "main", C("{\"limit\":3}"));
        JsonNode? appliedA = null, appliedB = null;
        var registry = new CliConfigRegistry(defaultStore);
        registry.Add(Provider("a", own, v => appliedA = v));
        registry.Add(Provider("b", null, v => appliedB = v));
        registry.Start();
        await registry.ReadyAsync();
        Assert.Equal((8.0, 3.0), (CliConfigValues.AsNumber(appliedA), CliConfigValues.AsNumber(appliedB)));
    }

    // ---------------------------------------------------------------- providers in different stores

    private sealed record Setup(CliConfigRegistry Registry, CliConfigService Service, ListAuditStore Audits, List<CliEvent> Events);

    private static Setup Make(ICliConfigStore? defaultStore, params ICliConfigProvider[] providers)
    {
        var registry = new CliConfigRegistry(defaultStore);
        foreach (var p in providers) registry.Add(p);
        registry.Start();
        var audits = new ListAuditStore();
        var bus = new CliEventBus();
        var events = new List<CliEvent>();
        bus.Subscribe("config:*", (e, _) => { events.Add(e); return Task.CompletedTask; });
        return new Setup(registry, new CliConfigService(registry) { AuditStore = () => audits, EventBus = () => bus }, audits, events);
    }

    [Fact]
    public async Task TwoProvidersInTwoStores_EachWriteAndReadTheirOwn_AJsonFileAndADatabase()
    {
        var dir = Directory.CreateTempSubdirectory("qcli-multi-").FullName;
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        try
        {
            var file = new JsonFileConfigStore(Path.Combine(dir, "app.json"), new JsonFileConfigStoreOptions { Section = "qcli" });
            var database = EfCoreConfigStoreTests.Create(connection);
            var s = Make(null, Provider("a", file), Provider("b", database));
            await s.Registry.ReadyAsync();
            await s.Service.UpdateAsync("a", "main", R("{\"limit\":2}"), All);
            await s.Service.UpdateAsync("b", "main", R("{\"limit\":3}"), All);
            Assert.Equal((2.0, 0L), (CliConfigValues.AsNumber((await file.ReadAsync("a", "main")).Values["limit"]), (await file.ReadAsync("b", "main")).Version));
            Assert.Equal((3.0, 0L), (CliConfigValues.AsNumber((await database.ReadAsync("b", "main")).Values["limit"]), (await database.ReadAsync("a", "main")).Version));
            Assert.Equal(["a"], JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "app.json")))!["qcli"]!.AsObject().Select(p => p.Key));
            var views = (await s.Service.ListAsync(All)).ToDictionary(v => v.Id);
            Assert.Equal(("file", "database"), (views["a"].Store!.Kind, views["b"].Store!.Kind));
            Assert.Equal(("1", 2.0), (views["a"].Sections[0].Version, CliConfigValues.AsNumber(views["a"].Sections[0].Entries[0].Value)));
            Assert.Equal(("file", "database"), (views["a"].Sections[0].Entries[0].Source, views["b"].Sections[0].Entries[0].Source));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task OneStoreSharedByTwoProviders_KeepsTheirDataApart()
    {
        var shared = new NamedStore("shared");
        var s = Make(null, Provider("a", shared), Provider("b", shared));
        await s.Service.UpdateAsync("a", "main", R("{\"limit\":2}"), All);
        await s.Service.UpdateAsync("b", "main", R("{\"name\":\"y\"}"), All);
        await s.Service.UpdateAsync("b", "main", R("{\"limit\":4}"), All);
        Assert.Equal(["a.main", "b.main", "b.main"], shared.Writes);
        Assert.Equal(("{\"limit\":2}", 1L), (ConfigStoreContractTests.Values(await shared.ReadAsync("a", "main")), (await shared.ReadAsync("a", "main")).Version));
        Assert.Equal(2, (await shared.ReadAsync("b", "main")).Version);
        Assert.Equal([s.Registry.DefaultStore, shared], s.Registry.Stores);
    }

    [Fact]
    public async Task VersionsAndConflicts_ArePerStore()
    {
        var one = new NamedStore("one");
        var two = new NamedStore("two");
        var s = Make(null, Provider("a", one), Provider("b", two));
        await s.Service.UpdateAsync("a", "main", R("{\"limit\":2}", "0"), All);
        await s.Service.UpdateAsync("a", "main", R("{\"limit\":3}", "1"), All);
        var conflict = await Assert.ThrowsAsync<CliConfigException>(() => s.Service.UpdateAsync("a", "main", R("{\"limit\":4}", "1"), All));
        Assert.Equal((409, "2"), (conflict.Status, conflict.Version));
        var b = await s.Service.UpdateAsync("b", "main", R("{\"limit\":9}", "0"), All);
        Assert.Equal("1", b.Version);
        Assert.Equal(["a.main", "a.main"], one.Writes);
        Assert.Equal(["b.main"], two.Writes);
    }

    [Fact]
    public async Task TheAuditEntryAndTheConfigChangedEvent_NameTheKindOfStoreWritten()
    {
        var redisLike = new NamedStore("Redis", CliConfigStoreKinds.Redis);
        var s = Make(null, Provider("a", redisLike), Provider("b"));
        await s.Service.UpdateAsync("a", "main", R("{\"limit\":2}"), All);
        await s.Service.UpdateAsync("b", "main", R("{\"limit\":2}"), All);
        Assert.Equal(["redis", "memory"], s.Audits.Entries.Select(e => e.Properties!["store"]));
        Assert.Equal(["provider", "section", "version", "store", "via", "changes"], s.Audits.Entries[0].Properties!.Keys);
        Assert.Equal(["redis", "memory"], s.Events.Select(e => e.Data["store"]));
    }

    [Fact]
    public async Task AWriterThatSavesItself_IsAuditedWithoutAStore()
    {
        var writer = new ScriptedWriter();
        writer.Updates.Enqueue(() => new CliConfigUpdateResult { Applied = ["a"], Version = "2" });
        var s = Make(null, writer);
        await s.Service.UpdateAsync("w", "s", R("{\"a\":1}"), All);
        Assert.False(s.Audits.Entries.Single().Properties!.ContainsKey("store"));
        Assert.False(s.Events.Single().Data.ContainsKey("store"));
        Assert.Null((await s.Service.GetAsync("w", All)).Store);
    }

    // ---------------------------------------------------------------- ready over every store

    [Fact]
    public async Task Ready_WaitsForEachStoresOwnReady_AndEveryProviderLoadingFromItsStore()
    {
        var gate = new TaskCompletionSource();
        var slow = new NamedStore("slow") { Ready = () => gate.Task };
        var defaultCalls = 0;
        var defaultStore = new NamedStore("default") { Ready = () => { defaultCalls++; return Task.CompletedTask; } };
        var s = Make(defaultStore, Provider("a", slow), Provider("b"));
        var ready = s.Registry.ReadyAsync();
        await Task.Delay(50);
        Assert.False(ready.IsCompleted);
        gate.SetResult();
        await ready;
        Assert.True(defaultCalls >= 1);
    }

    [Fact]
    public async Task Ready_FailsWhenOneOfTheStoresFails_EvenOneOnlyASingleProviderUses()
    {
        var broken = new NamedStore("broken") { Ready = () => Task.FromException(new InvalidOperationException("redis down")) };
        var s = Make(null, Provider("a"), Provider("b", broken));
        Assert.Equal("redis down", (await Assert.ThrowsAsync<InvalidOperationException>(() => s.Registry.ReadyAsync())).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service.ListAsync(All));
    }

    // ---------------------------------------------------------------- DI: options map, SetConfigStore(store, id), from the container

    [Fact]
    public async Task TheOptionsMap_AndSetConfigStoreForAnId_AreTheSame_SetConfigStoreWinsForTheSameId()
    {
        var a = new NamedStore("a");
        var b = new NamedStore("b");
        var c = new NamedStore("c");
        await using var host = await ConfigTestHost.StartAsync(cli =>
        {
            cli.ConfigureConfig(o => { o.Stores["billing"] = a; o.Stores["extra"] = c; });
            cli.SetConfigStore(b, "extra");
            cli.AddConfigProvider(Provider("billing"));
            cli.AddConfigProvider(CliCustomConfigProvider.Create("extra"));
            cli.AddConfigProvider(CliCustomConfigProvider.Create("third", "Third", store: c));
        });
        Assert.Equal("a", host.Config.DescribeProviderStore(host.Registry.Find("billing")!)!.Title);
        Assert.Equal("b", host.Config.DescribeProviderStore(host.Registry.Find("extra")!)!.Title);
        Assert.Equal("c", host.Config.DescribeProviderStore(host.Registry.Find("third")!)!.Title);
    }

    [Fact]
    public async Task SetConfigStoreOfAType_ResolvesFromTheContainer_OneInstanceForEveryIdAndTheDefault()
    {
        await using var host = await ConfigTestHost.StartAsync(cli =>
        {
            cli.SetConfigStore<ContainerStore>();
            cli.SetConfigStore<ContainerStore>("billing");
            cli.SetConfigStore<ContainerStore>("extra");
            cli.SetConfigStore(sp => new NamedStore("factory"), "third");
            cli.AddConfigProvider(Provider("billing"));
            cli.AddConfigProvider(CliCustomConfigProvider.Create("extra"));
            cli.AddConfigProvider(CliCustomConfigProvider.Create("third", "Third"));
        });
        var store = host.Service<ContainerStore>();
        Assert.Same(store, host.Registry.DefaultStore);
        Assert.Same(store, host.Registry.StoreOf("billing"));
        Assert.Same(store, host.Registry.StoreOf("extra"));
        Assert.Same(store, host.Registry.StoreOf("custom"));
        Assert.Equal("factory", ((NamedStore)host.Registry.StoreOf("third")!).Name);
        await host.Config.UpdateAsync("billing", "main", R("{\"limit\":2}"), All);
        await host.Config.UpdateAsync("extra", "custom", R("{\"k\":1}"), All);
        Assert.Equal(["billing.main", "extra.custom"], store.Writes);
    }

    [Fact]
    public async Task SetAppSettingsConfigStore_KeepsOneProviderInAppSettingsJson_InTheContentRoot()
    {
        var dir = Directory.CreateTempSubdirectory("qcli-appsettings-host-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), "{\n  \"Logging\": { \"LogLevel\": { \"Default\": \"Warning\" } }\n}\n");
            await using var host = await ConfigTestHost.StartAsync(cli =>
            {
                cli.SetAppSettingsConfigStore("billing", o => o.ContentRoot = dir);
                cli.SetAppSettingsConfigStore("extra", o => o.ContentRoot = dir);
                cli.AddConfigProvider(Provider("billing"));
                cli.AddConfigProvider(CliCustomConfigProvider.Create("extra"));
            });
            Assert.Same(host.Registry.StoreOf("billing"), host.Registry.StoreOf("extra"));
            var view = await host.Config.GetAsync("billing", All);
            Assert.Equal(("file", "appsettings.json", "section Qcli:Config"), (view.Store!.Kind, view.Store.Title, view.Store.Detail));
            Assert.Equal("memory", (await host.Config.GetAsync("custom", All)).Store!.Kind);
            await host.Config.UpdateAsync("billing", "main", R("{\"limit\":2}"), All);
            await host.Config.UpdateAsync("extra", "custom", R("{\"motd\":\"hi\"}"), All);
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "appsettings.json")))!;
            Assert.Equal("Warning", root["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(
                "{\"billing\":{\"main\":{\"version\":1,\"values\":{\"limit\":2}}},\"extra\":{\"custom\":{\"version\":1,\"values\":{\"motd\":\"hi\"}}}}"),
                root["Qcli"]!["Config"]));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---------------------------------------------------------------- the plugins' own stores

    [Fact]
    public async Task ThePlugins_GiveTheirProviderItsOwnStore_NoneByDefault_TheHostsStoreWins()
    {
        var jobs = new NamedStore("jobs-own");
        var users = new NamedStore("users-own");
        var portal = new NamedStore("portal-own");
        var logs = new NamedStore("logs-own");
        var hosts = new NamedStore("hosts");
        await using (var host = await ConfigTestHost.StartAsync(cli =>
        {
            cli.ConfigureJobs(o => o.ConfigStore = jobs);
            cli.AddUsers(o => o.ConfigStore = users);
            cli.AddPortal(o => o.ConfigStore = portal);
            cli.AddAdmin(o => o.LogsConfigStore = logs);
            cli.SetConfigStore(hosts, "portal");
        }))
        {
            Assert.Same(jobs, host.Registry.StoreOf("jobs"));
            Assert.Same(users, host.Registry.StoreOf("users"));
            Assert.Same(hosts, host.Registry.StoreOf("portal"));
            Assert.Same(portal, ((CliStoredConfigProvider)host.Registry.Find("portal")!).OwnStore);
            Assert.Same(logs, host.Registry.StoreOf("logs"));
        }
        await using (var plain = await ConfigTestHost.StartAsync(cli =>
        {
            cli.ConfigureJobs(_ => { });
            cli.AddUsers();
            cli.AddPortal();
            cli.AddAdmin();
        }))
        {
            foreach (var id in new[] { "jobs", "users", "portal", "logs" })
            {
                Assert.Null(((CliStoredConfigProvider)plain.Registry.Find(id)!).OwnStore);
                Assert.Same(plain.Registry.DefaultStore, plain.Registry.StoreOf(id));
            }
        }
    }
}

/// <summary>A server with providers in several stores: GET /config, PATCH, MCP and config list.</summary>
public sealed class ConfigMultiStoreServerTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("qcli-multi-server-").FullName;
    private readonly NamedStore _jobsOwn = new("jobs-own");
    private readonly NamedStore _logsStore = new("logs-store");
    private readonly NamedStore _redisLike = new("Redis", CliConfigStoreKinds.Redis);
    private string _db = null!;
    private ConfigTestHost _host = null!;

    public async Task InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_dir, "app.json"), "{\n  \"app\": {\n    \"name\": \"demo\"\n  }\n}\n");
        _db = Path.Combine(_dir, "qcli-demo.db");
        _host = await ConfigTestHost.StartAsync(cli =>
        {
            cli.AddMcp();
            cli.SetConfigStore(new JsonFileConfigStore(Path.Combine(_dir, "app.json"), new JsonFileConfigStoreOptions { Section = "qcli" }));
            // jobs: the host's store (the database) wins over the one the jobs plugin sets
            cli.ConfigureJobs(o => o.ConfigStore = _jobsOwn);
            cli.AddEfCoreConfigStore(o => o.UseSqlite($"Data Source={_db}"), providerId: "jobs");
            cli.ConfigureConfig(o => o.Stores["custom"] = _redisLike);
            cli.AddConfigProvider(ConfigMultiStoreTests.Provider("billing"));
            cli.SetConfigStore(_redisLike, "billing");
            cli.AddAdmin(o => o.LogsConfigStore = _logsStore);
        }, admin: false);
        await _host.Config.ReadyAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private const string File_ = "{\"kind\":\"file\",\"title\":\"JSON file\",\"detail\":\"app.json, section qcli\",\"persistent\":true}";
    private const string Redis = "{\"kind\":\"redis\",\"title\":\"Redis\",\"detail\":\"Redis-detail\",\"persistent\":true}";

    private async Task<JsonElement> GetAsync(string path) =>
        JsonSerializer.SerializeToElement(path.Length == 0
            ? new CliConfigListView { DefaultStore = _host.Config.DescribeDefaultStore(), Providers = (await _host.Config.ListAsync(ConfigTestHost.All)).ToList() }
            : (object)await _host.Config.GetAsync(path, ConfigTestHost.All));

    [Fact]
    public async Task GetConfig_DefaultStore_AndAStoreOnEveryStoredProvider_NoneOnReadOnlyOnes()
    {
        var body = await GetAsync("");
        Assert.Equal(["defaultStore", "providers"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(File_, body.GetProperty("defaultStore").GetRawText());
        var stores = body.GetProperty("providers").EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!,
            p => p.TryGetProperty("store", out var s) ? s.GetRawText() : null);
        Assert.Equal(["server", "auth", "host", "cors", "mcp", "jobs", "logs", "billing", "custom"], stores.Keys);
        Assert.Equal((null, null, null), (stores["server"], stores["auth"], stores["host"]));
        Assert.Equal((File_, File_), (stores["cors"], stores["mcp"]));
        Assert.Equal("{\"kind\":\"custom\",\"title\":\"logs-store\",\"detail\":\"logs-store-detail\",\"persistent\":true}", stores["logs"]);
        Assert.Equal((Redis, Redis), (stores["billing"], stores["custom"]));
        var jobs = ConfigApi.Find(body.GetProperty("providers"), "id", "jobs");
        Assert.Equal("{\"kind\":\"database\",\"title\":\"Database\",\"detail\":\"SQLite database qcli-demo.db, table QcliConfig\",\"persistent\":true}",
            jobs.GetProperty("store").GetRawText());
        Assert.Equal("{\"kind\":\"store\",\"description\":\"Changes are saved to its config store\",\"sources\":[]}", jobs.GetProperty("storage").GetRawText());
        Assert.Equal(["id", "title", "description", "order", "writable", "storage", "store", "sections"], jobs.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task GetProvider_TheProviderWithItsStore()
    {
        Assert.Equal("database", (await GetAsync("jobs")).GetProperty("store").GetProperty("kind").GetString());
        Assert.False((await GetAsync("server")).TryGetProperty("store", out _));
    }

    [Fact]
    public async Task Patch_WritesToTheProvidersOwnStoreOnly_AndTheRestOfTheAppSettingsFileStays()
    {
        await _host.Config.UpdateAsync("jobs", "history", new CliConfigUpdateRequest { Changes = ConfigStoreContractTests.C("{\"maxRunsPerJob\":25}"), Version = "0" }, ConfigTestHost.All);
        await _host.Config.UpdateAsync("logs", "buffer", new CliConfigUpdateRequest { Changes = ConfigStoreContractTests.C("{\"minLevel\":\"WARN\"}") }, ConfigTestHost.All);
        await _host.Config.UpdateAsync("custom", "custom", new CliConfigUpdateRequest { Changes = ConfigStoreContractTests.C("{\"motd\":\"hi\"}") }, ConfigTestHost.All);
        await _host.Config.UpdateAsync("cors", "cors", new CliConfigUpdateRequest { Changes = ConfigStoreContractTests.C("{\"allowedOrigins\":[\"https://a.example\"]}") }, ConfigTestHost.All);
        using (var connection = new SqliteConnection($"Data Source={_db}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"Provider\", \"Section\", \"Data\" FROM \"QcliConfig\"";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(("jobs", "history", "{\"maxRunsPerJob\":25}"), (reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            Assert.False(reader.Read());
        }
        Assert.Empty(_jobsOwn.Writes);
        Assert.Equal(["logs.buffer"], _logsStore.Writes);
        Assert.Equal(["custom.custom"], _redisLike.Writes);
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "app.json")))!;
        Assert.Equal("{\"name\":\"demo\"}", file["app"]!.ToJsonString());
        Assert.Equal(["cors"], file["qcli"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public async Task McpConfigListAndConfigGet_CarryEachProvidersStore()
    {
        var list = (await Mcp.CallAsync(_host.Client, "config_list", new { })).GetProperty("structuredContent");
        Assert.Equal("app.json, section qcli", list.GetProperty("defaultStore").GetProperty("detail").GetString());
        Assert.False(list.TryGetProperty("store", out _));
        var providers = list.GetProperty("providers");
        Assert.Equal("database", ConfigApi.Find(providers, "id", "jobs").GetProperty("store").GetProperty("kind").GetString());
        Assert.False(ConfigApi.Find(providers, "id", "server").TryGetProperty("store", out _));
        var get = (await Mcp.CallAsync(_host.Client, "config_get", new { provider = "billing" })).GetProperty("structuredContent");
        Assert.Equal(Redis, get.GetProperty("provider").GetProperty("store").GetRawText());
    }

    [Fact]
    public async Task ConfigList_TheDefaultStoreFirst_ThenEachStoredProviderWithItsStore()
    {
        var text = (await ConfigCommands.RunAsync(_host.Client, "config list")).Text;
        var heads = text.Split('\n').Where(l => !l.StartsWith("  ")).ToList();
        Assert.StartsWith("host  Host  (read-only; from Server options", heads[3]);
        heads.RemoveAt(3);
        Assert.Equal(
        [
            "Default store: JSON file (app.json, section qcli), kept across restarts",
            "server  Server  (read-only)",
            "auth  Authentication  (read-only)",
            "cors  CORS  · JSON file (app.json, section qcli)",
            "mcp  MCP  · JSON file (app.json, section qcli)",
            "jobs  Jobs  · Database (SQLite database qcli-demo.db, table QcliConfig)",
            "logs  Logs  · logs-store (logs-store-detail)",
            "billing  Billing  · Redis (Redis-detail)",
            "custom  Custom  · Redis (Redis-detail)",
        ], heads);
    }
}
