using System.Reflection;
using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.Config.Redis;
using StackExchange.Redis;

namespace Qodalis.Cli.Tests.Config;

/// <summary>
/// The commands the store uses, in memory: HGETALL, and EVAL of the write script (checked to be that script) with its
/// compare-and-set semantics. <see cref="BeforeEval"/> runs before the next script, to let a test write in between
/// (another server).
/// </summary>
internal sealed class FakeRedis(string where = "cache:6379/0") : IRedisConfigClient
{
    public Dictionary<string, Dictionary<string, string>> Hashes { get; } = new();
    public Dictionary<string, long> Ttls { get; } = new();
    public List<string> Calls { get; } = [];
    public Func<Task>? BeforeEval { get; set; }
    public Func<RedisResult>? Reply { get; set; }
    public int Connects { get; private set; }

    public string Where => where;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Connects++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> HashGetAllAsync(string key)
    {
        lock (Calls)
        {
            Calls.Add($"HGETALL {key}");
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                Hashes.TryGetValue(key, out var hash) ? new Dictionary<string, string>(hash) : new Dictionary<string, string>());
        }
    }

    public async Task<RedisResult> EvaluateAsync(string script, string key, RedisValue[] args)
    {
        Assert.Equal(RedisConfigStore.WriteScript, script);
        var hook = BeforeEval;
        BeforeEval = null;
        if (hook != null) await hook();
        if (Reply != null) return Reply();
        lock (Calls)
        {
            Calls.Add($"EVAL {key} {args[0]}");
            if (!Hashes.TryGetValue(key, out var hash)) hash = new Dictionary<string, string>();
            var current = hash.TryGetValue("version", out var v) ? long.Parse(v) : 0;
            if (current != (long)args[0]) return Result(0, current);
            var next = current + 1;
            hash["version"] = next.ToString();
            hash["values"] = args[1].ToString();
            hash["updatedAt"] = args[2].ToString();
            Hashes[key] = hash;
            if ((long)args[3] > 0) Ttls[key] = (long)args[3];
            return Result(1, next);
        }
    }

    public static RedisResult Result(long ok, long version) =>
        RedisResult.Create([RedisResult.Create((RedisValue)ok), RedisResult.Create((RedisValue)version)]);
}

/// <summary>The store contract on a fake Redis.</summary>
public sealed class RedisConfigStoreContractTests : ConfigStoreContractTests
{
    protected override ICliConfigStore Store { get; } = new RedisConfigStore(new RedisConfigStoreOptions { Client = new FakeRedis() });
}

/// <summary>Skipped unless <c>QCLI_TEST_REDIS</c> names a Redis (<c>redis://localhost:6379/15</c>).</summary>
public sealed class RealRedisFactAttribute : FactAttribute
{
    public static string? Url => Environment.GetEnvironmentVariable("QCLI_TEST_REDIS");

    public RealRedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Url)) Skip = "set QCLI_TEST_REDIS (redis://localhost:6379/15) to run against a real Redis";
    }
}

public sealed class RedisConfigStoreTests
{
    private static Dictionary<string, JsonNode?> C(string json) => ConfigStoreContractTests.C(json);

    private static (RedisConfigStore Store, FakeRedis Redis) Make(Action<RedisConfigStoreOptions>? configure = null, TimeProvider? time = null)
    {
        var redis = new FakeRedis();
        var options = new RedisConfigStoreOptions { Client = redis };
        configure?.Invoke(options);
        return (new RedisConfigStore(options, time), redis);
    }

    [Fact]
    public void TheWriteScript_IsTheNodeServers()
    {
        Assert.Equal(
            "\nlocal current = tonumber(redis.call('HGET', KEYS[1], 'version') or '0') or 0\nif current ~= tonumber(ARGV[1]) then\n    return {0, current}\nend\n" +
            "local nextVersion = current + 1\nredis.call('HSET', KEYS[1], 'version', nextVersion, 'values', ARGV[2], 'updatedAt', ARGV[3])\n" +
            "local ttl = tonumber(ARGV[4]) or 0\nif ttl > 0 then\n    redis.call('EXPIRE', KEYS[1], ttl)\nend\nreturn {1, nextVersion}\n",
            RedisConfigStore.WriteScript);
    }

    [Fact]
    public async Task OneHashPerProviderSection_UnderTheKeyPrefix_ValuesAsJson_Version_UpdatedAt()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        var (store, redis) = Make(time: time);
        await store.WriteAsync("jobs", "history", C("{\"maxAge\":\"30d\",\"list\":[1]}"));
        Assert.Equal(["qcli:config:jobs:history"], redis.Hashes.Keys);
        var hash = redis.Hashes["qcli:config:jobs:history"];
        Assert.Equal(("{\"maxAge\":\"30d\",\"list\":[1]}", "1", "2026-11-25T10:00:00.000Z"), (hash["values"], hash["version"], hash["updatedAt"]));
        Assert.Equal(["HGETALL qcli:config:jobs:history", "EVAL qcli:config:jobs:history 0"], redis.Calls);
        Assert.Equal("qcli:config:jobs:history", store.KeyOf("jobs", "history"));
        var (custom, customRedis) = Make(o => o.KeyPrefix = "app:");
        await custom.WriteAsync("p", "s", C("{\"a\":1}"));
        Assert.Equal(["app:p:s"], customRedis.Hashes.Keys);
    }

    [Fact]
    public void IsTheDatabaseSource_Persistent()
    {
        var (store, _) = Make();
        Assert.Equal("database", store.Source);
        Assert.True(store.Describe()!.Persistent);
    }

    [Fact]
    public void DescribesItself_ByHostPortDatabaseAndPrefix()
    {
        var (store, _) = Make();
        Assert.Equal("{\"kind\":\"redis\",\"title\":\"Redis\",\"detail\":\"cache:6379/0, prefix qcli:config\",\"persistent\":true}",
            System.Text.Json.JsonSerializer.Serialize(store.Describe()));
        Assert.Equal("Redis (cache:6379/0, prefix qcli:config), kept across restarts", CliConfigStoreHelpers.DescribeLine(store.Describe()!));
        Assert.Equal("cache:6379/0", Make(o => o.KeyPrefix = "").Store.Describe()!.Detail);
    }

    [Fact]
    public void Describe_NeverShowsThePasswordOrUserOfAUrl()
    {
        using var a = new RedisConfigStore(new RedisConfigStoreOptions { Url = "redis://admin:s3cret@redis.internal:6380/4" });
        Assert.Equal("redis.internal:6380/4, prefix qcli:config", a.Describe()!.Detail);
        using var b = new RedisConfigStore(new RedisConfigStoreOptions { Url = "rediss://:s3cret@cache" });
        Assert.Equal("cache:6379/0, prefix qcli:config", b.Describe()!.Detail);
        using var c = new RedisConfigStore(new RedisConfigStoreOptions { Url = "cache:6390,password=s3cret,defaultDatabase=2" });
        Assert.Equal("cache:6390/2, prefix qcli:config", c.Describe()!.Detail);
        foreach (var store in new[] { a, b, c })
        {
            var json = System.Text.Json.JsonSerializer.Serialize(store.Describe());
            Assert.DoesNotContain("s3cret", json);
            Assert.DoesNotContain("admin", json);
        }
    }

    [Fact]
    public void ParsesRedisUrls_IntoStackExchangeOptions()
    {
        var o = RedisConfigClient.ParseUrl("rediss://admin:p%40ss@redis.internal:6380/4");
        Assert.Equal((true, "admin", "p@ss", 4, false), (o.Ssl, o.User, o.Password, o.DefaultDatabase, o.AbortOnConnectFail));
        var plain = RedisConfigClient.ParseUrl("redis://:pw@cache");
        Assert.Equal((false, null, "pw", null), (plain.Ssl, plain.User, plain.Password, plain.DefaultDatabase));
        Assert.Throws<ArgumentException>(() => RedisConfigClient.ParseUrl("redis://cache/notanumber"));
    }

    [Fact]
    public void NeedsAUrlAConnectionOrAClient_RefusesABadTtl()
    {
        Assert.Equal("RedisConfigStore needs a Url, a Connection or a Client",
            Assert.Throws<ArgumentException>(() => new RedisConfigStore(new RedisConfigStoreOptions())).Message);
        Assert.Throws<ArgumentException>(() => Make(o => o.TtlSeconds = -1));
    }

    [Fact]
    public async Task AWriteThatLostTheRace_WithAnExpectedVersion_IsAConflictCarryingTheVersionThatWon()
    {
        var (store, redis) = Make();
        await store.WriteAsync("p", "s", C("{\"a\":1}"));
        redis.BeforeEval = () => new RedisConfigStore(new RedisConfigStoreOptions { Client = redis }).WriteAsync("p", "s", C("{\"other\":true}"));
        var ex = await Assert.ThrowsAsync<CliConfigConflictException>(() => store.WriteAsync("p", "s", C("{\"a\":2}"), 1));
        Assert.Equal("2", ex.CurrentVersion);
        var read = await store.ReadAsync("p", "s");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"a\":1,\"other\":true}"), read.ValuesToJson()));
    }

    [Fact]
    public async Task AWriteWithoutAnExpectedVersionThatLostTheRace_IsRetriedOnTheNewValues_NoChangeLost()
    {
        var (store, redis) = Make();
        await store.WriteAsync("p", "s", C("{\"a\":1}"));
        redis.BeforeEval = () => new RedisConfigStore(new RedisConfigStoreOptions { Client = redis }).WriteAsync("p", "s", C("{\"b\":2}"));
        var result = await store.WriteAsync("p", "s", C("{\"c\":3}"));
        Assert.Equal(3, result.Version);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"a\":1,\"b\":2,\"c\":3}"), result.ValuesToJson()));
        Assert.Equal(2, redis.Calls.Count(c => c == "EVAL qcli:config:p:s 1"));
    }

    [Fact]
    public async Task GivesUpAfterMaxRetries_WhenTheSectionKeepsChanging()
    {
        var (store, redis) = Make(o => o.MaxRetries = 2);
        redis.Reply = () => FakeRedis.Result(0, 99);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync("p", "s", C("{\"a\":1}")));
        Assert.Equal("config section p.s: too many concurrent writes", ex.Message);
        Assert.Equal(3, redis.Calls.Count(c => c.StartsWith("HGETALL")));
    }

    [Fact]
    public async Task TtlSeconds_ExpiresASectionAfterItsLastChange_NoneByDefault()
    {
        var (withTtl, a) = Make(o => o.TtlSeconds = 3600);
        await withTtl.WriteAsync("p", "s", C("{\"a\":1}"));
        Assert.Equal(3600, a.Ttls["qcli:config:p:s"]);
        var (without, b) = Make();
        await without.WriteAsync("p", "s", C("{\"a\":1}"));
        Assert.Empty(b.Ttls);
    }

    [Fact]
    public async Task UnreadableValuesOrVersions_ReadAsEmpty()
    {
        var (store, redis) = Make();
        redis.Hashes["qcli:config:p:s"] = new() { ["values"] = "{nope", ["version"] = "x" };
        redis.Hashes["qcli:config:p:t"] = new() { ["values"] = "[1,2]", ["version"] = "3" };
        var s = await store.ReadAsync("p", "s");
        Assert.Equal(("{}", 0L), (ConfigStoreContractTests.Values(s), s.Version));
        var t = await store.ReadAsync("p", "t");
        Assert.Equal(("{}", 3L), (ConfigStoreContractTests.Values(t), t.Version));
    }

    [Fact]
    public async Task AnUnexpectedScriptReply_IsAnError()
    {
        var (store, redis) = Make();
        redis.Reply = () => RedisResult.Create((RedisValue)"OK");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync("p", "s", C("{\"a\":1}")));
        Assert.StartsWith("unexpected reply from Redis", ex.Message);
        redis.Reply = () => FakeRedis.Result(7, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync("p", "s", C("{\"a\":1}")));
    }

    [Fact]
    public async Task ReadyConnects_AndDisposeEndsOnlyAConnectionItOpened()
    {
        var (store, redis) = Make();
        await store.ReadyAsync();
        Assert.Equal(1, redis.Connects);
        await store.DisposeAsync();
        Assert.Same(redis, store.Client);
        await using var fromUrl = new RedisConfigStore(new RedisConfigStoreOptions { Url = "redis://127.0.0.1:1/0" });
        Assert.IsType<RedisConfigClient>(fromUrl.Client);
    }

    [Fact]
    public async Task IsListedAsAnInstalledPluginPackage_AndAddRedisConfigStoreSetsTheStore()
    {
        var redis = new FakeRedis();
        await using var host = await ConfigTestHost.StartAsync(cli =>
        {
            cli.AddConfigProvider(ConfigMultiStoreTests.Provider("billing"));
            cli.AddRedisConfigStore(o => o.Client = redis, providerId: "billing");
            cli.SetConfigStore<RedisConfigStore>("extra");
            cli.AddConfigProvider(CliCustomConfigProvider.Create("extra"));
        });
        Assert.Contains(host.Service<CliInstalledPlugins>().All, p => p.Id == "Qodalis.Cli.Plugin.Config.Redis");
        var store = host.Service<RedisConfigStore>();
        Assert.Same(store, host.Registry.StoreOf("billing"));
        Assert.Same(store, host.Registry.StoreOf("extra"));
        Assert.Equal("memory", CliConfigStoreHelpers.Describe(host.Registry.DefaultStore).Kind);
        Assert.True(redis.Connects >= 1);
    }

    [Fact]
    public async Task OneProviderInRedis_TheRestInTheDefaultStore_GetConfigAndConfigListShowIt()
    {
        var redis = new FakeRedis();
        await using var host = await ConfigTestHost.StartAsync(cli =>
        {
            cli.AddConfigProvider(ConfigMultiStoreTests.Provider("billing"));
            cli.SetConfigStore(new RedisConfigStore(new RedisConfigStoreOptions { Client = redis }), "billing");
        });
        var billing = await host.Config.GetAsync("billing", ConfigTestHost.All);
        Assert.Equal(("redis", "Redis", "cache:6379/0, prefix qcli:config"), (billing.Store!.Kind, billing.Store.Title, billing.Store.Detail));
        Assert.Equal("memory", (await host.Config.GetAsync("custom", ConfigTestHost.All)).Store!.Kind);
        var response = await host.Config.UpdateAsync("billing", "main", new CliConfigUpdateRequest { Changes = C("{\"limit\":9}"), Version = "0" }, ConfigTestHost.All);
        Assert.Equal("1", response.Version);
        Assert.Equal("{\"limit\":9}", redis.Hashes["qcli:config:billing:main"]["values"]);
        var stale = await Assert.ThrowsAsync<CliConfigException>(() =>
            host.Config.UpdateAsync("billing", "main", new CliConfigUpdateRequest { Changes = C("{\"limit\":3}"), Version = "0" }, ConfigTestHost.All));
        Assert.Equal((409, "1"), (stale.Status, stale.Version));
        var text = (await ConfigCommands.RunAsync(host.Client, "config list")).Text;
        Assert.Contains("\nbilling  Billing  · Redis (cache:6379/0, prefix qcli:config)\n", text);
        Assert.StartsWith("Default store: In memory, changes are lost on restart", text);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>Against a real Redis (<c>QCLI_TEST_REDIS</c>): the store contract and two servers racing on one section.</summary>
public sealed class RealRedisConfigStoreTests
{
    private sealed class Contract(RedisConfigStore store) : ConfigStoreContractTests
    {
        protected override ICliConfigStore Store => store;
    }

    private static async Task<(ConnectionMultiplexer Connection, string Prefix)> ConnectAsync()
    {
        var connection = await ConnectionMultiplexer.ConnectAsync(RedisConfigClient.ParseUrl(RealRedisFactAttribute.Url!));
        return (connection, $"qcli-test:{Guid.NewGuid():N}:");
    }

    private static async Task CleanAsync(ConnectionMultiplexer connection, string prefix)
    {
        var options = RedisConfigClient.ParseUrl(RealRedisFactAttribute.Url!);
        var db = connection.GetDatabase(options.DefaultDatabase ?? -1);
        foreach (var endPoint in connection.GetEndPoints())
        {
            await foreach (var key in connection.GetServer(endPoint).KeysAsync(options.DefaultDatabase ?? 0, $"{prefix}*"))
                await db.KeyDeleteAsync(key);
        }
        await connection.DisposeAsync();
    }

    [RealRedisFact]
    public async Task PassesTheStoreContract()
    {
        var (connection, prefix) = await ConnectAsync();
        try
        {
            var db = RedisConfigClient.ParseUrl(RealRedisFactAttribute.Url!).DefaultDatabase;
            var tests = typeof(ConfigStoreContractTests).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<FactAttribute>() != null).ToList();
            Assert.NotEmpty(tests);
            foreach (var test in tests)
            {
                var store = new RedisConfigStore(new RedisConfigStoreOptions { Connection = connection, Database = db, KeyPrefix = $"{prefix}{test.Name}:" });
                await (Task)test.Invoke(new Contract(store), null)!;
            }
        }
        finally
        {
            await CleanAsync(connection, prefix);
        }
    }

    [RealRedisFact]
    public async Task TwoServersRacingOnOneSection_NoChangeLost_StaleVersionsRefused()
    {
        var (connection, prefix) = await ConnectAsync();
        try
        {
            var stores = Enumerable.Range(0, 2).Select(_ => new RedisConfigStore(new RedisConfigStoreOptions { Url = RealRedisFactAttribute.Url, KeyPrefix = prefix })).ToList();
            await Task.WhenAll(Enumerable.Range(0, 10).Select(i => stores[i % 2].WriteAsync("p", "s", ConfigStoreContractTests.C($"{{\"k{i}\":{i}}}"))));
            var final = await stores[0].ReadAsync("p", "s");
            Assert.Equal(10, final.Version);
            Assert.Equal(10, final.Values.Count);
            await Assert.ThrowsAsync<CliConfigConflictException>(() => stores[1].WriteAsync("p", "s", ConfigStoreContractTests.C("{\"x\":1}"), 3));
            foreach (var store in stores) await store.DisposeAsync();
        }
        finally
        {
            await CleanAsync(connection, prefix);
        }
    }
}
