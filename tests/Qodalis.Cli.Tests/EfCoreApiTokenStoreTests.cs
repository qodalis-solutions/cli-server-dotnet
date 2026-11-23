using Microsoft.Data.Sqlite;
using Qodalis.Cli.Plugin.Migrations.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Users.EfCore;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

public class EfCoreApiTokenStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ManualTimeProvider _time = new();
    private readonly EfCoreApiTokenStore _store;

    public EfCoreApiTokenStoreTests()
    {
        _connection.Open();
        // A host's own table: the database exists, so the store must add its table next to it
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE host_things (id INTEGER PRIMARY KEY)";
            command.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<ApiTokenStorageDbContext>().UseSqlite(_connection).Options;
        _store = new EfCoreApiTokenStore(new SqliteContextFactory<ApiTokenStorageDbContext>(options, o => new ApiTokenStorageDbContext(o)), _time);
    }

    public void Dispose() => _connection.Dispose();

    private ApiToken Token(string id, string userId, long createdAt, long? expiresAt = null, List<string>? permissions = null) => new()
    {
        Id = id,
        UserId = userId,
        Name = $"token {id}",
        TokenHash = $"hash-{id}",
        CreatedAt = createdAt,
        ExpiresAt = expiresAt,
        Permissions = permissions,
    };

    [Fact]
    public async Task CreatesTheTableInAnExistingDatabase_AndRoundTrips()
    {
        await _store.CreateTokenAsync(Token("t1", "u1", 1000, permissions: ["commands:execute"]));

        var byHash = await _store.GetTokenByHashAsync("hash-t1");
        var byId = await _store.GetTokenAsync("t1");

        Assert.Equal("u1", byHash!.UserId);
        Assert.Equal(["commands:execute"], byId!.Permissions);
        Assert.Null(byId.ExpiresAt);
        Assert.Null(await _store.GetTokenByHashAsync("hash-none"));
    }

    [Fact]
    public async Task ListsOldestFirst_PerUser_AndForgetsExpiredTokens()
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        await _store.CreateTokenAsync(Token("b", "u1", 2000));
        await _store.CreateTokenAsync(Token("a", "u1", 1000));
        await _store.CreateTokenAsync(Token("c", "u2", 1500));
        await _store.CreateTokenAsync(Token("old", "u1", 500, expiresAt: now - 1));

        Assert.Equal(["a", "b"], (await _store.GetTokensAsync("u1")).Select(t => t.Id));
        Assert.Equal(["a", "c", "b"], (await _store.GetTokensAsync()).Select(t => t.Id));
        Assert.Null(await _store.GetTokenAsync("old"));
    }

    [Fact]
    public async Task UpdatesLastUse_AndDeletesOneOrEveryTokenOfAUser()
    {
        await _store.CreateTokenAsync(Token("a", "u1", 1000));
        await _store.CreateTokenAsync(Token("b", "u1", 2000));
        await _store.CreateTokenAsync(Token("c", "u2", 3000));

        await _store.UpdateLastUsedAsync("c", 4242);
        await _store.DeleteTokenAsync("a");
        await _store.DeleteUserTokensAsync("u1");

        Assert.Equal(4242, (await _store.GetTokenAsync("c"))!.LastUsedAt);
        Assert.Equal(["c"], (await _store.GetTokensAsync()).Select(t => t.Id));
    }

    /// <summary>The renames <c>AddEfCoreApiTokenStore</c> registers by default.</summary>
    private static readonly QcliEfStoreRenames<ApiTokenStorageDbContext> Renames = new(new Dictionary<string, string> { ["api_tokens"] = "QcliApiTokens" });

    /// <summary>A store on <paramref name="connection"/> as <c>AddEfCoreApiTokenStore</c> makes it (renaming <c>api_tokens</c>).</summary>
    private EfCoreApiTokenStore UpgradingStore(SqliteConnection connection) =>
        new(new SqliteContextFactory<ApiTokenStorageDbContext>(
            new DbContextOptionsBuilder<ApiTokenStorageDbContext>().UseSqlite(connection).Options, o => new ApiTokenStorageDbContext(o)), _time, null, Renames);

    /// <summary>The <c>api_tokens</c> table as 1.0.0-beta.28 created it (no <c>Audience</c>), with one row.</summary>
    private static void CreateOldTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE api_tokens (
                Id TEXT NOT NULL PRIMARY KEY, UserId TEXT NOT NULL, Name TEXT NOT NULL, TokenHash TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL, ExpiresAt INTEGER NULL, LastUsedAt INTEGER NULL, Permissions TEXT NULL);
            INSERT INTO api_tokens (Id, UserId, Name, TokenHash, CreatedAt, ExpiresAt, LastUsedAt, Permissions)
            VALUES ('old', 'u1', 'old token', 'hash-old', 1000, NULL, NULL, '["files:read"]');
            """;
        command.ExecuteNonQuery();
    }

    private static List<string> Columns(SqliteConnection connection, string table = "QcliApiTokens")
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(0));
        return columns;
    }

    [Fact]
    public async Task RoundTripsTheAudience()
    {
        var host = Token("h", "u1", 1000, permissions: ["rates:read"]);
        host.Audience = ApiTokenAudience.Host;
        await _store.CreateTokenAsync(host);
        await _store.CreateTokenAsync(Token("c", "u1", 2000));

        Assert.Equal("host", (await _store.GetTokenByHashAsync("hash-h"))!.Audience);
        Assert.Equal("host", (await _store.GetTokenAsync("h"))!.Audience);
        Assert.Equal(["host", "cli"], (await _store.GetTokensAsync("u1")).Select(t => t.Audience));
    }

    [Fact]
    public async Task RenamesAnOldTable_KeepsItsTokens_AndAddsTheAudienceColumn()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        CreateOldTable(connection);
        var store = UpgradingStore(connection);

        var old = await store.GetTokenAsync("old");
        Assert.Equal("cli", old!.Audience);
        Assert.Equal(["files:read"], old.Permissions);

        var host = Token("h", "u1", 2000, permissions: ["rates:read"]);
        host.Audience = ApiTokenAudience.Host;
        await store.CreateTokenAsync(host);
        Assert.Equal("host", (await store.GetTokenByHashAsync("hash-h"))!.Audience);
        Assert.Contains("Audience", Columns(connection));
        Assert.Empty(Columns(connection, "api_tokens"));
    }

    [Fact]
    public async Task ProbeRunsOnce_AndToleratesAConcurrentAdd()
    {
        // A shared in-memory database: each context gets its own connection, as two server instances would.
        var connectionString = $"Data Source=tokens-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        CreateOldTable(keepAlive);
        var options = new DbContextOptionsBuilder<ApiTokenStorageDbContext>().UseSqlite(connectionString).Options;
        EfCoreApiTokenStore NewStore() =>
            new(new SqliteContextFactory<ApiTokenStorageDbContext>(options, o => new ApiTokenStorageDbContext(o)), _time, null, Renames);
        var first = NewStore();
        var second = NewStore();

        var tokens = await Task.WhenAll(first.GetTokenAsync("old"), second.GetTokenAsync("old"));

        Assert.All(tokens, t => Assert.Equal("cli", t!.Audience));
        Assert.Single(Columns(keepAlive), c => c == "Audience");
    }

    [Fact]
    public async Task WithUpgradeSchemaOff_AnOldTableIsNotAltered_AndTheStoreFailsClearly()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        CreateOldTable(connection);
        // The host's migrations own the table and kept its name
        var names = new EfCoreApiTokenStoreOptions { UpgradeSchema = false, LegacyTableNames = true };
        var options = new DbContextOptionsBuilder<ApiTokenStorageDbContext>().UseSqlite(connection)
            .UseQcliTables(QcliEfTables.Resolve(names, new Dictionary<string, string> { ["ApiTokens"] = "api_tokens" })).Options;
        var store = new EfCoreApiTokenStore(new SqliteContextFactory<ApiTokenStorageDbContext>(options, o => new ApiTokenStorageDbContext(o)),
            _time, names, Renames);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetTokenAsync("old"));

        Assert.Contains("Audience", ex.Message);
        Assert.Contains("UpgradeSchema", ex.Message);
        Assert.DoesNotContain("Audience", Columns(connection, "api_tokens"));

        // Once the host's migration added the column, the same store works.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "ALTER TABLE api_tokens ADD Audience TEXT NULL";
            command.ExecuteNonQuery();
        }
        Assert.Equal("cli", (await store.GetTokenAsync("old"))!.Audience);
    }

    [Fact]
    public void AddEfCoreApiTokenStore_PassesTheOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddCli(cli =>
        {
            cli.AddUsers();
            cli.AddEfCoreApiTokenStore(o => o.UseSqlite(_connection), o => o.UpgradeSchema = false);
        });

        var store = Assert.IsType<EfCoreApiTokenStore>(services.BuildServiceProvider().GetRequiredService<IApiTokenStore>());
        Assert.False(store.Options.UpgradeSchema);
    }

    [Fact]
    public async Task UpgradeUsesTheModelsColumnType()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        CreateOldTable(connection);
        var store = UpgradingStore(connection);

        await store.GetTokenAsync("old");

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type FROM pragma_table_info('QcliApiTokens') WHERE name = 'Audience'";
        Assert.Equal("TEXT", command.ExecuteScalar());
    }

    [Fact]
    public void ReplacesTheUsersPluginsInMemoryStore_BeforeOrAfterAddUsers()
    {
        foreach (var storeFirst in new[] { true, false })
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddControllers().AddCli(cli =>
            {
                if (storeFirst) cli.AddEfCoreApiTokenStore(o => o.UseSqlite(_connection));
                cli.AddUsers();
                if (!storeFirst) cli.AddEfCoreApiTokenStore(o => o.UseSqlite(_connection));
            });

            Assert.IsType<EfCoreApiTokenStore>(services.BuildServiceProvider().GetRequiredService<IApiTokenStore>());
        }
    }
}
