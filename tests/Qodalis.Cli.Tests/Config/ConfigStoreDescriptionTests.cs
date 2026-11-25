using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Config;
using Qodalis.Cli.Plugin.Config.EfCore;
using Qodalis.Cli.Plugin.Databases.EfCore;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests.Config;

/// <summary>A store that does not describe itself (a custom store named after its class).</summary>
internal sealed class VaultConfigStore(string source = CliConfigSources.Database) : ICliConfigStore
{
    private readonly InMemoryConfigStore _inner = new();

    public string Source { get; } = source;

    public Task<CliConfigStoredSection> ReadAsync(string provider, string section, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(provider, section, cancellationToken);

    public Task<CliConfigStoredSection> WriteAsync(string provider, string section, IReadOnlyDictionary<string, JsonNode?> changes,
        long? expectedVersion = null, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(provider, section, changes, expectedVersion, cancellationToken);
}

/// <summary>A store with its own description.</summary>
internal sealed class RedisConfigStore : ICliConfigStore
{
    private readonly InMemoryConfigStore _inner = new();

    public string Source => CliConfigSources.Database;

    public CliConfigStoreDescription? Describe() =>
        new() { Kind = CliConfigStoreKinds.Database, Title = "Redis", Detail = "db 3", Persistent = true };

    public Task<CliConfigStoredSection> ReadAsync(string provider, string section, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(provider, section, cancellationToken);

    public Task<CliConfigStoredSection> WriteAsync(string provider, string section, IReadOnlyDictionary<string, JsonNode?> changes,
        long? expectedVersion = null, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(provider, section, changes, expectedVersion, cancellationToken);
}

/// <summary>
/// Config stores describe themselves (memory, JSON file relative or by name, database by provider, name and table with
/// credentials and connection strings never shown, custom stores), and providers say where their settings come from.
/// </summary>
public sealed class ConfigStoreDescriptionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("qcli-config-describe-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private static string Json(CliConfigStoreDescription d) => JsonSerializer.Serialize(d);

    [Fact]
    public void InMemory_DescribesItself_LostOnRestart()
    {
        var d = CliConfigStoreHelpers.Describe(new InMemoryConfigStore());
        Assert.Equal("{\"kind\":\"memory\",\"title\":\"In memory\",\"detail\":\"\",\"persistent\":false}", Json(d));
        Assert.Equal("In memory, changes are lost on restart", CliConfigStoreHelpers.DescribeLine(d));
    }

    [Fact]
    public void AStoreWithoutADescription_IsACustomStoreNamedAfterItsClass_PersistentUnlessRuntime()
    {
        Assert.Equal("{\"kind\":\"custom\",\"title\":\"Custom store\",\"detail\":\"VaultConfigStore\",\"persistent\":true}",
            Json(CliConfigStoreHelpers.Describe(new VaultConfigStore())));
        Assert.False(CliConfigStoreHelpers.Describe(new VaultConfigStore(CliConfigSources.Runtime)).Persistent);
        Assert.Equal("Custom store (VaultConfigStore), kept across restarts",
            CliConfigStoreHelpers.DescribeLine(CliConfigStoreHelpers.Describe(new VaultConfigStore())));
    }

    [Fact]
    public void ACustomDescription_IsUsedAsGiven()
    {
        Assert.Equal("Redis (db 3), kept across restarts", CliConfigStoreHelpers.DescribeLine(CliConfigStoreHelpers.Describe(new RedisConfigStore())));
    }

    [Fact]
    public void JsonFile_ByThePathRelativeToTheApp_OrTheFileNameOnlyOutsideIt()
    {
        Assert.Equal("{\"kind\":\"file\",\"title\":\"JSON file\",\"detail\":\"qcli-demo-config.json\",\"persistent\":true}",
            Json(new JsonFileConfigStore(Path.Combine(_dir, "qcli-demo-config.json")).Describe()!));
        Assert.Equal("data/config.json",
            new JsonFileConfigStore(Path.Combine(Directory.GetCurrentDirectory(), "data", "config.json")).Describe()!.Detail);
        Assert.Equal("qcli-demo-config.json", new JsonFileConfigStore("qcli-demo-config.json").Describe()!.Detail);
        Assert.Equal("JSON file (qcli-demo-config.json), kept across restarts",
            CliConfigStoreHelpers.DescribeLine(new JsonFileConfigStore(Path.Combine(_dir, "qcli-demo-config.json")).Describe()!));
    }

    [Fact]
    public void EfCore_ByProviderDatabaseAndTable_NeverTheConnectionString()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Assert.Equal("{\"kind\":\"database\",\"title\":\"Database\",\"detail\":\"SQLite, table QcliConfig\",\"persistent\":true}",
            Json(EfCoreConfigStoreTests.Create(connection).Describe()!));

        // A SQLite file by its name only, with the prefixed table
        var file = Path.Combine(_dir, "qcli.db");
        var prefixed = QcliEfTables.Resolve(new Qodalis.Cli.Abstractions.Helpers.QcliTableNameOptions { TablePrefix = "App" },
            new Dictionary<string, string> { ["Config"] = "QcliConfig" });
        var options = new DbContextOptionsBuilder<ConfigStorageDbContext>().UseSqlite($"Data Source={file}").UseQcliTables(prefixed).Options;
        var named = new EfCoreConfigStore(new SqliteContextFactory<ConfigStorageDbContext>(options, o => new ConfigStorageDbContext(o)));
        var detail = named.Describe()!.Detail;
        Assert.Equal("SQLite database qcli.db, table AppConfig", detail);
        Assert.DoesNotContain(_dir, detail);
    }

    [Fact]
    public void EfCore_DatabaseNames_FromTheConnection_CredentialsNeverShown()
    {
        using var sqlServer = new SqlConnection("Server=db.internal,1433;Database=qcli;User Id=sa;Password=s3cr3t;TrustServerCertificate=true");
        Assert.Equal("qcli", EfCoreConfigStore.DatabaseName(sqlServer));
        using var mysql = new MySqlConnection("Server=db.internal;Database=shop;User=root;Password=s3cr3t");
        Assert.Equal("shop", EfCoreConfigStore.DatabaseName(mysql));
        using var memory = new SqliteConnection("Data Source=:memory:");
        Assert.Null(EfCoreConfigStore.DatabaseName(memory));
        Assert.Equal(("SQL Server", "PostgreSQL", "MySQL", "SQLite", "In-memory database"),
            (EfCoreConfigStore.ProviderTitle("Microsoft.EntityFrameworkCore.SqlServer"), EfCoreConfigStore.ProviderTitle("Npgsql.EntityFrameworkCore.PostgreSQL"),
             EfCoreConfigStore.ProviderTitle("Pomelo.EntityFrameworkCore.MySql"), EfCoreConfigStore.ProviderTitle("Microsoft.EntityFrameworkCore.Sqlite"),
             EfCoreConfigStore.ProviderTitle("Microsoft.EntityFrameworkCore.InMemory")));
    }

    [Fact]
    public void EfCore_ANonRelationalProvider_IsDescribedWithoutADatabaseName()
    {
        var options = new DbContextOptionsBuilder<ConfigStorageDbContext>().UseInMemoryDatabase("qcli-describe").Options;
        var store = new EfCoreConfigStore(new SqliteContextFactory<ConfigStorageDbContext>(options, o => new ConfigStorageDbContext(o)));
        var d = store.Describe()!;
        Assert.Equal(("database", true), (d.Kind, d.Persistent));
        Assert.StartsWith("In-memory database, table ", d.Detail);
    }

    [Fact]
    public void ProviderStorage_ItsOwn_ElseCustomSavedOrReadByTheProvider()
    {
        Assert.Equal("{\"kind\":\"store\",\"description\":\"Changes are saved to its config store\",\"sources\":[]}",
            JsonSerializer.Serialize(CliConfigViews.Storage(ConfigApi.Billing())));
        Assert.Equal("{\"kind\":\"runtime\",\"description\":\"Read from the running server process\",\"sources\":[]}",
            JsonSerializer.Serialize(CliConfigViews.Storage(new CliServerConfigProvider())));
        Assert.Equal("{\"kind\":\"options\",\"description\":\"Server options and environment variables\",\"sources\":[\"Server options\",\"Environment variables\"]}",
            JsonSerializer.Serialize(CliConfigViews.Storage(new CliHostConfigProvider(() => []))));
        Assert.Equal(("custom", "Read by the provider"), StorageOf(new PlainProvider()));
        Assert.Equal(("custom", "Saved by the provider"), StorageOf(new PlainWriter()));
    }

    private static (string, string) StorageOf(ICliConfigProvider provider)
    {
        var s = CliConfigViews.Storage(provider);
        return (s.Kind, s.Description);
    }

    [Fact]
    public void HostStorage_ListsTheConfigurationSourcesInOrder_FileNamesOnly()
    {
        var app = Path.Combine(_dir, "app");
        Directory.CreateDirectory(app);
        var secrets = Path.Combine(_dir, "usersecrets", "0f3c");
        Directory.CreateDirectory(secrets);
        var root = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["contentRoot"] = app })
            .AddEnvironmentVariables("DOTNET_")
            .AddJsonFile(Path.Combine(app, "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(app, "appsettings.Development.json"), optional: true)
            .AddJsonFile(Path.Combine(secrets, "secrets.json"), optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(["--Qcli:Mode=demo"])
            .Build();
        var storage = CliConfigViews.Storage(new CliHostConfigProvider(() => [], root));
        Assert.Equal("configuration", storage.Kind);
        Assert.Equal(["Server options", "appsettings.json", "appsettings.Development.json", "User secrets", "Environment variables", "Command line"],
            storage.Sources);
        Assert.DoesNotContain(storage.Sources, s => s.Contains(_dir, StringComparison.Ordinal));

        // A chained configuration is expanded
        var chained = new ConfigurationBuilder().AddConfiguration(root).AddJsonFile(Path.Combine(app, "extra.json"), optional: true).Build();
        Assert.Equal(["appsettings.json", "appsettings.Development.json", "User secrets", "Environment variables", "Command line", "extra.json"],
            CliHostConfigProvider.ConfigurationSources(chained));
    }

    private sealed class PlainProvider : ICliConfigProvider
    {
        public string Id => "plain";
        public string Title => "Plain";

        public Task<IReadOnlyList<CliConfigSection>> GetSectionsAsync(CliConfigContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CliConfigSection>>([]);
    }

    private sealed class PlainWriter : ICliConfigProvider, ICliConfigWriter
    {
        public string Id => "writer";
        public string Title => "Writer";

        public Task<IReadOnlyList<CliConfigSection>> GetSectionsAsync(CliConfigContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CliConfigSection>>([]);

        public Task<IReadOnlyDictionary<string, string>> ValidateAsync(string sectionId, IReadOnlyDictionary<string, JsonNode?> changes,
            CliConfigContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<CliConfigUpdateResult> UpdateAsync(string sectionId, IReadOnlyDictionary<string, JsonNode?> changes,
            string? expectedVersion, CliConfigContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CliConfigUpdateResult());
    }
}
