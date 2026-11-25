using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Plugin.Config.EfCore;
using Qodalis.Cli.Plugin.Databases.EfCore;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests.Config;

/// <summary>The behaviour every store shares: versions, conflicts, removals, copies.</summary>
public abstract class ConfigStoreContractTests
{
    protected abstract ICliConfigStore Store { get; }

    internal static Dictionary<string, JsonNode?> C(string json) =>
        JsonNode.Parse(json)!.AsObject().ToDictionary(p => p.Key, p => p.Value?.DeepClone());

    internal static string Values(CliConfigStoredSection section) => section.ValuesToJson().ToJsonString();

    [Fact]
    public async Task AnUnknownSection_IsEmptyAtVersion0()
    {
        var section = await Store.ReadAsync("p", "s");
        Assert.Equal("{}", Values(section));
        Assert.Equal(0, section.Version);
    }

    [Fact]
    public async Task Writes_BumpTheVersionByOne_AndKeepTheValues()
    {
        var first = await Store.WriteAsync("p", "s", C("{\"a\":1,\"b\":[\"x\"]}"));
        Assert.Equal("{\"a\":1,\"b\":[\"x\"]}", Values(first));
        Assert.Equal(1, first.Version);
        var second = await Store.WriteAsync("p", "s", C("{\"c\":{\"deep\":true}}"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"a\":1,\"b\":[\"x\"],\"c\":{\"deep\":true}}"), second.ValuesToJson()));
        Assert.Equal(2, second.Version);
        var read = await Store.ReadAsync("p", "s");
        Assert.True(JsonNode.DeepEquals(second.ValuesToJson(), read.ValuesToJson()));
        Assert.Equal(2, read.Version);
    }

    [Fact]
    public async Task Null_RemovesAKey()
    {
        await Store.WriteAsync("p", "s", C("{\"a\":1,\"b\":2}"));
        var after = await Store.WriteAsync("p", "s", C("{\"a\":null}"));
        Assert.Equal("{\"b\":2}", Values(after));
        Assert.Equal(2, after.Version);
    }

    [Fact]
    public async Task SectionsAndProviders_AreIndependent()
    {
        await Store.WriteAsync("p", "s1", C("{\"a\":1}"));
        await Store.WriteAsync("p", "s2", C("{\"a\":2}"));
        await Store.WriteAsync("q", "s1", C("{\"a\":3}"));
        Assert.Equal("{\"a\":1}", Values(await Store.ReadAsync("p", "s1")));
        Assert.Equal("{\"a\":2}", Values(await Store.ReadAsync("p", "s2")));
        Assert.Equal("{\"a\":3}", Values(await Store.ReadAsync("q", "s1")));
        Assert.Equal(1, (await Store.ReadAsync("q", "s1")).Version);
    }

    [Fact]
    public async Task AStaleExpectedVersion_IsAConflictCarryingTheCurrentVersion()
    {
        await Store.WriteAsync("p", "s", C("{\"a\":1}"), 0);
        await Store.WriteAsync("p", "s", C("{\"a\":2}"), 1);
        var ex = await Assert.ThrowsAsync<CliConfigConflictException>(() => Store.WriteAsync("p", "s", C("{\"a\":3}"), 1));
        Assert.Equal("2", ex.CurrentVersion);
        Assert.Equal("{\"a\":2}", Values(await Store.ReadAsync("p", "s")));
    }

    [Fact]
    public async Task AFirstWriteWithAnExpectedVersionOtherThan0_IsAConflict()
    {
        await Assert.ThrowsAsync<CliConfigConflictException>(() => Store.WriteAsync("p", "new", C("{\"a\":1}"), 3));
        Assert.Equal(0, (await Store.ReadAsync("p", "new")).Version);
    }

    [Fact]
    public async Task ReturnedValues_AreCopies()
    {
        var written = await Store.WriteAsync("p", "s", C("{\"list\":[\"a\"]}"));
        written.Values["list"]!.AsArray().Add("mutated");
        var read = await Store.ReadAsync("p", "s");
        read.Values["list"]!.AsArray().Add("again");
        Assert.Equal("{\"list\":[\"a\"]}", Values(await Store.ReadAsync("p", "s")));
    }

    [Fact]
    public async Task Secrets_AreStoredAsGiven()
    {
        await Store.WriteAsync("p", "s", C("{\"apiKey\":\"sk_live_1\"}"));
        Assert.Equal("sk_live_1", CliConfigValues.AsString((await Store.ReadAsync("p", "s")).Values["apiKey"]));
    }
}

public class InMemoryConfigStoreTests : ConfigStoreContractTests
{
    protected override ICliConfigStore Store { get; } = new InMemoryConfigStore();

    [Fact]
    public void IsTheRuntimeSource() => Assert.Equal("runtime", Store.Source);

    [Fact]
    public void ApplyChanges_SetsReplacesAndRemoves_WithoutTouchingTheInput()
    {
        var values = C("{\"a\":1,\"b\":2}");
        var next = CliConfigStoreHelpers.ApplyChanges(values, C("{\"a\":3,\"b\":null,\"c\":\"x\"}"));
        Assert.Equal("{\"a\":3,\"c\":\"x\"}", new CliConfigStoredSection(next, 0).ValuesToJson().ToJsonString());
        Assert.Equal("{\"a\":1,\"b\":2}", new CliConfigStoredSection(values, 0).ValuesToJson().ToJsonString());
    }
}

public sealed class JsonFileConfigStoreContractTests : ConfigStoreContractTests, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("qcli-config-").FullName;

    public JsonFileConfigStoreContractTests() => Store = new JsonFileConfigStore(Path.Combine(_dir, "config.json"));

    protected override ICliConfigStore Store { get; }

    public void Dispose() => Directory.Delete(_dir, true);
}

public sealed class JsonFileConfigStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("qcli-config-").FullName;

    public void Dispose()
    {
        foreach (var sub in Directory.GetDirectories(_dir))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(sub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task IsTheFileSource_AndPersistsAcrossInstances_InTheDocumentedShape()
    {
        var file = Path.Combine(_dir, "nested", "config.json");
        var store = new JsonFileConfigStore(file);
        Assert.Equal("file", store.Source);
        await store.WriteAsync("custom", "custom", ConfigStoreContractTests.C("{\"motd\":\"hi\"}"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"custom\":{\"custom\":{\"values\":{\"motd\":\"hi\"},\"version\":1}}}"),
            JsonNode.Parse(await File.ReadAllTextAsync(file))));
        var again = await new JsonFileConfigStore(file).ReadAsync("custom", "custom");
        Assert.Equal("{\"motd\":\"hi\"}", ConfigStoreContractTests.Values(again));
        Assert.Equal(1, again.Version);
    }

    [Fact]
    public async Task WritesTheFileReadableByTheOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        var file = Path.Combine(_dir, "config.json");
        await new JsonFileConfigStore(file).WriteAsync("p", "s", ConfigStoreContractTests.C("{\"secret\":\"x\"}"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.Equal(["config.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AMissingOrEmptyFile_IsEmpty()
    {
        var file = Path.Combine(_dir, "config.json");
        Assert.Equal(0, (await new JsonFileConfigStore(file).ReadAsync("p", "s")).Version);
        await File.WriteAllTextAsync(file, "  \n");
        Assert.Equal(0, (await new JsonFileConfigStore(file).ReadAsync("p", "s")).Version);
    }

    [Fact]
    public async Task RefusesAFileThatIsNotJson()
    {
        var file = Path.Combine(_dir, "config.json");
        await File.WriteAllTextAsync(file, "{oops");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new JsonFileConfigStore(file).ReadAsync("p", "s"));
        Assert.Contains("is not valid JSON", ex.Message);
    }

    [Fact]
    public async Task IgnoresMalformedSectionsAndVersions()
    {
        var file = Path.Combine(_dir, "config.json");
        await File.WriteAllTextAsync(file, "{\"p\":{\"s\":{\"values\":[1],\"version\":-3},\"t\":\"x\",\"u\":{\"values\":{\"a\":1},\"version\":2}},\"q\":[]}");
        var store = new JsonFileConfigStore(file);
        var s = await store.ReadAsync("p", "s");
        Assert.Equal(("{}", 0L), (ConfigStoreContractTests.Values(s), s.Version));
        Assert.Equal(0, (await store.ReadAsync("p", "t")).Version);
        var u = await store.ReadAsync("p", "u");
        Assert.Equal(("{\"a\":1}", 2L), (ConfigStoreContractTests.Values(u), u.Version));
    }

    [Fact]
    public async Task AFailedWrite_LeavesTheFileAndTheCacheAsTheyWere()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        var sub = Path.Combine(_dir, "ro");
        Directory.CreateDirectory(sub);
        var store = new JsonFileConfigStore(Path.Combine(sub, "config.json"));
        await store.WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":1}"));
        File.SetUnixFileMode(sub, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        await Assert.ThrowsAnyAsync<Exception>(() => store.WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":2}")));
        var read = await store.ReadAsync("p", "s");
        Assert.Equal(("{\"a\":1}", 1L), (ConfigStoreContractTests.Values(read), read.Version));
    }
}

public sealed class EfCoreConfigStoreContractTests : ConfigStoreContractTests, IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public EfCoreConfigStoreContractTests()
    {
        _connection.Open();
        Store = EfCoreConfigStoreTests.Create(_connection);
    }

    protected override ICliConfigStore Store { get; }

    public void Dispose() => _connection.Dispose();
}

public sealed class EfCoreConfigStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public EfCoreConfigStoreTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    internal static EfCoreConfigStore Create(SqliteConnection connection, QcliEfTables? tables = null)
    {
        var builder = new DbContextOptionsBuilder<ConfigStorageDbContext>().UseSqlite(connection);
        if (tables != null) builder.UseQcliTables(tables);
        return new EfCoreConfigStore(new SqliteContextFactory<ConfigStorageDbContext>(builder.Options, o => new ConfigStorageDbContext(o)));
    }

    private List<string> Tables()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private void Exec(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task IsTheDatabaseSource_AndCreatesQcliConfigInAnExistingDatabase()
    {
        Exec("CREATE TABLE host_things (id INTEGER PRIMARY KEY)");
        var store = Create(_connection);
        Assert.Equal("database", store.Source);
        await store.ReadAsync("p", "s");
        Assert.Contains("QcliConfig", Tables());
        Assert.Contains("host_things", Tables());
    }

    [Fact]
    public async Task StoresOneRowPerSection_WithItsValuesAsJson()
    {
        await Create(_connection).WriteAsync("cors", "cors", ConfigStoreContractTests.C("{\"allowedOrigins\":[\"https://a.example\"]}"));
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT \"Provider\", \"Section\", \"Version\", \"Data\" FROM \"QcliConfig\"";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(("cors", "cors", 1L), (reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        Assert.Equal("{\"allowedOrigins\":[\"https://a.example\"]}", reader.GetString(3));
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task PersistsAcrossStoresOnTheSameDatabase()
    {
        await Create(_connection).WriteAsync("p", "s", ConfigStoreContractTests.C("{\"x\":1}"));
        var read = await Create(_connection).ReadAsync("p", "s");
        Assert.Equal(("{\"x\":1}", 1L), (ConfigStoreContractTests.Values(read), read.Version));
    }

    [Fact]
    public async Task TablePrefixAndPerTableName()
    {
        var prefixed = QcliEfTables.Resolve(new Qodalis.Cli.Abstractions.Helpers.QcliTableNameOptions { TablePrefix = "App" },
            new Dictionary<string, string> { ["Config"] = "QcliConfig" });
        await Create(_connection, prefixed).WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":1}"));
        Assert.Contains("AppConfig", Tables());
    }

    [Fact]
    public async Task AConflict_LeavesTheRowAsItWas_AndTheStoreUsable()
    {
        var store = Create(_connection);
        await store.WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":1}"));
        await Assert.ThrowsAsync<CliConfigConflictException>(() => store.WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":2}"), 0));
        Assert.Equal("{\"a\":1}", ConfigStoreContractTests.Values(await store.ReadAsync("p", "s")));
        Assert.Equal(2, (await store.WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":3}"), 1)).Version);
    }

    [Fact]
    public async Task CorruptData_ReadsAsEmpty()
    {
        var store = Create(_connection);
        await store.WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":1}"));
        Exec("UPDATE \"QcliConfig\" SET \"Data\" = '{nope'");
        var read = await store.ReadAsync("p", "s");
        Assert.Equal(("{}", 1L), (ConfigStoreContractTests.Values(read), read.Version));
    }

    [Fact]
    public async Task ConcurrentWriters_NeverLoseAChange()
    {
        var file = Path.Combine(Path.GetTempPath(), $"qcli-config-{Guid.NewGuid():N}.db");
        try
        {
            var stores = Enumerable.Range(0, 2).Select(_ =>
            {
                var options = new DbContextOptionsBuilder<ConfigStorageDbContext>().UseSqlite($"Data Source={file};Default Timeout=30").Options;
                return new EfCoreConfigStore(new SqliteContextFactory<ConfigStorageDbContext>(options, o => new ConfigStorageDbContext(o)));
            }).ToList();
            await stores[0].WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":1}"));
            await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
                stores[i % 2].WriteAsync("p", "s", ConfigStoreContractTests.C($"{{\"k{i}\":{i}}}"))));
            var final = await stores[1].ReadAsync("p", "s");
            Assert.Equal(5, final.Version);
            Assert.Equal(["a", "k0", "k1", "k2", "k3"], final.Values.Keys.Order());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }
}
