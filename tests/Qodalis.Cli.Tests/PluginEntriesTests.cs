using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Config;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Audit;
using Qodalis.Cli.Plugin.Aws;
using Qodalis.Cli.Plugin.Aws.Processors;
using Qodalis.Cli.Plugin.DataExplorer.Postgres;
using Qodalis.Cli.Plugin.DataExplorer.Sql;
using Qodalis.Cli.Plugin.Audit.EfCore;
using Qodalis.Cli.Plugin.Config.EfCore;
using Qodalis.Cli.Plugin.Config.Redis;
using Qodalis.Cli.Plugin.Databases;
using Qodalis.Cli.Plugin.Databases.EfCore;
using Qodalis.Cli.Plugin.Docs;
using Qodalis.Cli.Plugin.FileSystem.EfCore;
using Qodalis.Cli.Plugin.Logs.Seq;
using Qodalis.Cli.Plugin.Portal;
using Qodalis.Cli.Plugin.PublicApi;
using Qodalis.Cli.Plugin.Users;
using Qodalis.Cli.Plugin.Jobs;
using Qodalis.Cli.Plugin.Jobs.EfCore;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Plugin.FileSystem.Json;
using Qodalis.Cli.Plugin.FileSystem.S3;
using Qodalis.Cli.Plugin.FileSystem.Sqlite;
using Qodalis.Cli.Plugin.Weather;
using Qodalis.Cli.Plugins;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests;

/// <summary>The official plugins' entries, added in code (the same entries the marketplace installs).</summary>
[Collection("ServerPlugins")]
public class PluginEntriesTests : IAsyncLifetime
{
    private readonly List<string> _dirs = [];
    private readonly List<IHost> _hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.StopAsync();
            host.Dispose();
        }
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
        var dir = Path.Combine(Path.GetTempPath(), $"qcli-plugin-entries-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private async Task<(IHost Host, string Dir)> StartAsync(Action<CliBuilder> configure, string? dir = null)
    {
        dir ??= TempDir();
        var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(s => s.AddControllers().AddCli(cli =>
            {
                cli.ConfigureServerPlugins(o => o.DataDir = dir);
                configure(cli);
            }));
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(e => e.MapControllers());
                app.UseCli();
            });
        }).StartAsync();
        _hosts.Add(host);
        await host.Services.GetRequiredService<CliConfigRegistry>().ReadyAsync();
        return (host, dir);
    }

    private static Task SetAsync(IHost host, string provider, string key, JsonNode? value) =>
        host.Services.GetRequiredService<ICliConfigService>().UpdateAsync(provider, "settings",
            new CliConfigUpdateRequest { Changes = new Dictionary<string, JsonNode?> { [key] = value } }, new CliConfigContext(null, _ => true));

    private static async Task<IReadOnlyList<CliServerPluginState>> States(IHost host)
    {
        var plugins = host.Services.GetRequiredService<CliServerPlugins>();
        await plugins.CheckSettingsAsync();
        return plugins.States();
    }

    private static Task<string> Run(IHost host, string command, string? value = null)
    {
        var processor = host.Services.GetRequiredService<ICliCommandRegistry>().FindProcessor(command)!;
        return processor.HandleAsync(new CliProcessCommand { Command = command, Value = value });
    }

    [Fact]
    public async Task Weather_TheDefaultLocationIsASetting_ReadOnEveryCommand()
    {
        var (host, _) = await StartAsync(cli => cli.AddPlugin<WeatherPluginEntry>(new { defaultLocation = "Paris" }));
        var weather = (CliWeatherCommandProcessor)host.Services.GetRequiredService<ICliCommandRegistry>().FindProcessor("weather")!;
        Assert.Equal("weather", (await States(host)).Single().Name);
        Assert.Equal("Paris", CliWeatherCommandProcessor.GetLocation(new CliProcessCommand { Command = "weather" }, ReadFallback(host)));
        await SetAsync(host, "weather", "defaultLocation", "Oslo");
        Assert.Equal("Oslo", ReadFallback(host));
        Assert.NotNull(weather);
    }

    private static string ReadFallback(IHost host)
    {
        var provider = host.Services.GetServices<ICliConfigProvider>().OfType<CliStoredConfigProvider>().Single(p => p.Id == "weather");
        return CliWeatherCommandProcessor.Fallback(() => provider.Text("defaultLocation"));
    }

    [Fact]
    public async Task Aws_RegionProfileAndKeys_AreAppliedAtStartAndWhenChanged_TheKeysSecrets()
    {
        var (host, _) = await StartAsync(cli => cli.AddPlugin<AwsPluginEntry>(new { region = "eu-north-1" }));
        var aws = host.Services.GetServices<ICliCommandProcessor>().OfType<AwsCommandProcessor>().Single();
        Assert.Equal("eu-north-1", aws.ConfigService.GetConfigSummary()["Region"]);
        await SetAsync(host, "aws", "profile", "work");
        await SetAsync(host, "aws", "accessKeyId", "AKIAEXAMPLEKEY123");
        await SetAsync(host, "aws", "secretAccessKey", "s3cret");
        var summary = aws.ConfigService.GetConfigSummary();
        Assert.Equal(("work", "****"), (summary["Profile"], summary["Secret Access Key"]));
        Assert.StartsWith("AKIA", summary["Access Key ID"]);
        var view = await host.Services.GetRequiredService<ICliConfigService>().GetAsync("aws", new CliConfigContext(null, _ => true));
        var key = view.Sections.Single().Entries.Single(e => e.Key == "secretAccessKey");
        Assert.Equal((true, null, true), (key.Sensitive, key.Value, key.IsSet));
    }

    [Fact]
    public async Task Docs_ServesTheGuidesOfItsFolder_AtItsPath_WithoutTheHostsMiddleware()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "plugin-data", "docs", "guides"));
        File.WriteAllText(Path.Combine(dir, "plugin-data", "docs", "guides", "start.md"), "---\ntitle: Start\n---\nHello {{ name }}");
        var (host, _) = await StartAsync(cli => cli.AddPlugin<DocsPluginEntry>(new { folder = "guides", basePath = "/help", name = "Acme" }), dir);
        var client = host.GetTestClient();
        var list = await client.GetStringAsync("/help/pages");
        Assert.Contains("start", list);
        Assert.Contains("Hello Acme", await client.GetStringAsync("/help/pages/start"));
        Assert.Contains("start", await Run(host, "docs"));
    }

    [Fact]
    public async Task Audit_KeepsTheLogInMemory_WithItsSizeASetting()
    {
        var (host, _) = await StartAsync(cli => cli.AddPlugin<AuditPluginEntry>(new { maxEntries = 250 }));
        var store = host.Services.GetRequiredKeyedService<IAuditStore>("Qodalis.Cli.Plugin.Audit.InnerStore");
        Assert.Equal(250, Assert.IsType<InMemoryAuditStore>(store).MaxEntries);
        Assert.Contains(host.Services.GetRequiredService<Qodalis.Cli.Models.CliInstalledPlugins>().All, p => p.Id == "Qodalis.Cli.Plugin.Audit" && p.Source == "code");
    }

    [Fact]
    public async Task FileSystem_FoldersRequired_NeedsConfigurationUntilSet_ReadOnlyByDefault()
    {
        var root = TempDir();
        File.WriteAllText(Path.Combine(root, "a.txt"), "A");
        var (host, _) = await StartAsync(cli => cli.AddPlugin<FileSystemPluginEntry>());
        var state = (await States(host)).Single();
        Assert.Equal((CliServerPluginStatuses.NeedsConfiguration, "filesystem"), (state.Status, state.Name));
        Assert.Equal(["settings.roots"], state.Missing);
        var storage = host.Services.GetRequiredService<IFileStorageProvider>();
        await Assert.ThrowsAsync<FileStorageNotConfiguredError>(() => storage.ListAsync(root));

        var (configured, _) = await StartAsync(cli => cli.AddPlugin<FileSystemPluginEntry>(new { roots = new[] { root } }));
        var files = configured.Services.GetRequiredService<IFileStorageProvider>();
        Assert.Equal("A", await files.ReadFileAsync(Path.Combine(root, "a.txt")));
        await Assert.ThrowsAsync<FileStorageReadOnlyError>(() => files.WriteFileAsync(Path.Combine(root, "b.txt"), "B"));
        Assert.Equal(CliServerPluginStatuses.Loaded, (await States(configured)).Single().Status);
    }

    [Fact]
    public async Task JsonFile_WritableWhenReadOnlyIsOff_InThePluginsDataDir_AndOneStorageOnly()
    {
        var (host, dir) = await StartAsync(cli => cli.AddPlugin<JsonFilePluginEntry>(new { readOnly = false }));
        var files = host.Services.GetRequiredService<IFileStorageProvider>();
        await files.MkdirAsync("/docs");
        await files.WriteFileAsync("/docs/a.txt", "A");
        Assert.Equal("A", await files.ReadFileAsync("/docs/a.txt"));
        Assert.True(File.Exists(Path.Combine(dir, "plugin-data", "filesystem-json", "files.json")));

        var (two, _) = await StartAsync(cli =>
        {
            cli.AddPlugin<JsonFilePluginEntry>();
            cli.AddPlugin<SqlitePluginEntry>();
        });
        var states = await States(two);
        Assert.Equal(CliServerPluginStatuses.Loaded, states.Single(s => s.Name == "filesystem-json").Status);
        var sqlite = states.Single(s => s.Name == "filesystem-sqlite");
        Assert.Equal((CliServerPluginStatuses.Failed, "the filesystem storage is already set by the host or another plugin"), (sqlite.Status, sqlite.Error));
    }

    [Fact]
    public async Task Sqlite_AndS3_FromTheirSettings_S3NeedsABucket()
    {
        var (sqlite, dir) = await StartAsync(cli => cli.AddPlugin<SqlitePluginEntry>(new { readOnly = false }));
        var files = sqlite.Services.GetRequiredService<IFileStorageProvider>();
        await files.WriteFileAsync("/a.txt", "A");
        Assert.Equal("A", await files.ReadFileAsync("/a.txt"));
        Assert.True(File.Exists(Path.Combine(dir, "plugin-data", "filesystem-sqlite", "files.db")));

        var (s3, _) = await StartAsync(cli => cli.AddPlugin<S3PluginEntry>());
        var state = (await States(s3)).Single();
        Assert.Equal(["settings.bucket"], state.Missing);
        await Assert.ThrowsAsync<FileStorageNotConfiguredError>(() => s3.Services.GetRequiredService<IFileStorageProvider>().ListAsync("/"));
        var view = await s3.Services.GetRequiredService<ICliConfigService>().GetAsync("filesystem-s3", new CliConfigContext(null, _ => true));
        Assert.True(view.Sections.Single().Entries.Single(e => e.Key == "bucket").Required);
    }

    [Fact]
    public async Task DataExplorerSources_AddedOnceConfigured_TheDataExplorersRulesOnTheLaterOnes()
    {
        var (host, dir) = await StartAsync(cli =>
        {
            // The host's own source keeps its rules; the plugins' sources follow the data explorer entry's
            cli.AddDataExplorerProvider(new SqlDataExplorerProvider("Data Source=:memory:"), o => { o.Name = "host"; o.Description = "h"; o.ReadOnly = false; o.MaxRows = 5000; });
            cli.AddPlugin<SqlPluginEntry>(new { name = "local", readOnly = false });
            cli.AddPlugin<PostgresPluginEntry>();
            cli.AddPlugin<DataExplorerPluginEntry>(new { forceReadOnly = true, maxRows = 50, rateLimitQueries = 10 });
        });
        var states = await States(host);
        // The data explorer (Core) registers before its sources (Source), whatever order they were added in
        var providers = host.Services.GetServices<ICliConfigProvider>().Select(p => p.Id).Where(id => id.StartsWith("data-explorer", StringComparison.Ordinal));
        Assert.Equal(["data-explorer", "data-explorer-sql", "data-explorer-postgres"], providers);
        var postgres = states.Single(s => s.Name == "data-explorer-postgres");
        Assert.Equal((CliServerPluginStatuses.NeedsConfiguration, "data-explorer-postgres"), (postgres.Status, postgres.Settings));
        Assert.Equal(["settings.connectionString"], postgres.Missing);

        var registry = host.Services.GetRequiredService<Qodalis.Cli.Services.DataExplorerRegistry>();
        Assert.Equal(["host", "local"], registry.GetSources().Select(s => s.Name).Order());
        var local = registry.Get("local")!.Value.Options;
        Assert.Equal((true, 50, 10), (local.ReadOnly, local.MaxRows, local.RateLimit!.MaxQueries));
        var own = registry.Get("host")!.Value.Options;
        Assert.Equal((false, 5000, null), (own.ReadOnly, own.MaxRows, own.RateLimit));
        Assert.True(File.Exists(Path.Combine(dir, "plugin-data", "data-explorer-sql", "data.db")) || registry.Get("local") != null);
    }

    [Fact]
    public async Task Jobs_AndItsDatabaseStorage_FromTheirEntries()
    {
        var (host, dir) = await StartAsync(cli =>
        {
            cli.AddPlugin<JobsPluginEntry>();
            cli.AddPlugin<JobsEfCorePluginEntry>(new { connectionString = "jobs.db" });
        });
        Assert.NotNull(host.Services.GetService<ICliJobScheduler>());
        Assert.IsType<EfCoreJobStorageProvider>(host.Services.GetRequiredService<Qodalis.Cli.Abstractions.Jobs.ICliJobStorageProvider>());
        Assert.All(await States(host), s => Assert.Equal(CliServerPluginStatuses.Loaded, s.Status));

        var (memory, _) = await StartAsync(cli =>
        {
            cli.AddPlugin<JobsPluginEntry>();
            cli.AddPlugin<JobsEfCorePluginEntry>();
        });
        Assert.IsNotType<EfCoreJobStorageProvider>(memory.Services.GetRequiredService<Qodalis.Cli.Abstractions.Jobs.ICliJobStorageProvider>());
        var storage = (await States(memory)).Single(s => s.Name == "jobs-efcore");
        Assert.Equal(["settings.connectionString"], storage.Missing);
    }

    [Fact]
    public async Task EfStores_ReadTheirConnectionAtStart_FromPluginSettingsJson_AfterASavedSetting()
    {
        var dir = TempDir();
        var (first, _) = await StartAsync(cli => cli.AddPlugin<AuditEfCorePluginEntry>(), dir);
        Assert.Null(first.Services.GetService<IAuditStore>());
        Assert.Equal(CliServerPluginStatuses.NeedsConfiguration, (await States(first)).Single().Status);
        await SetAsync(first, "audit-efcore", "connectionString", "audit.db");
        Assert.Contains("audit-efcore", File.ReadAllText(Path.Combine(dir, CliServerPlugins.SettingsFile)));

        var (next, _) = await StartAsync(cli => cli.AddPlugin<AuditEfCorePluginEntry>(), dir);
        Assert.IsType<EfCoreAuditStore>(next.Services.GetRequiredKeyedService<IAuditStore>("Qodalis.Cli.Plugin.Audit.InnerStore"));
        Assert.Equal(CliServerPluginStatuses.Loaded, (await States(next)).Single().Status);
    }

    [Fact]
    public async Task AnEfStoreWhoseProviderIsNotInstalled_Fails_AndTheServerStarts()
    {
        // The postgres provider (Npgsql.EntityFrameworkCore.PostgreSQL) is not referenced: the plugin fails while it
        // registers, instead of the server's start failing when the store is first resolved
        var (host, _) = await StartAsync(cli => cli.AddPlugin<AuditEfCorePluginEntry>(new { dialect = "postgres", connectionString = "Host=localhost" }));
        var state = (await States(host)).Single();
        Assert.Equal(CliServerPluginStatuses.Failed, state.Status);
        Assert.StartsWith("the postgres provider is not installed", state.Error);
        Assert.Contains("install the database-postgres plugin", state.Error);
        Assert.Null(host.Services.GetService<IAuditStore>());
        // Its settings stay, so the dialect can be corrected before the next restart
        Assert.Equal("audit-efcore", state.Settings);
        await SetAsync(host, "audit-efcore", "dialect", "sqlite");

        Assert.Throws<InvalidOperationException>(() => CliEfDialects.EnsureInstalled("postgres"));
        Assert.Throws<InvalidOperationException>(() => CliEfDialects.EnsureInstalled("oracle"));
        CliEfDialects.EnsureInstalled("sqlite");
    }

    /// <summary>A driver plugin of the dialect the tests have (sqlite), as database-postgres and database-mssql are.</summary>
    [CliPlugin(typeof(SqliteDriverEntry), Id = "database-sqlite", Order = CliPluginOrder.Driver)]
    public sealed class SqliteDriverEntry() : CliDatabaseDriverPluginEntry("sqlite", "SQLite", "Microsoft.Data.Sqlite", "Microsoft.EntityFrameworkCore.Sqlite");

    public sealed class BrokenDriverEntry() : CliDatabaseDriverPluginEntry("postgres", "PostgreSQL", "Acme.Nowhere");

    [Fact]
    public async Task ADriverWithoutAConnection_NeedsConfiguration_ItsDialectStillRegistered()
    {
        CliBuilder? builder = null;
        var (host, _) = await StartAsync(cli =>
        {
            builder = cli;
            cli.AddPlugin<SqliteDriverEntry>(id: "database-sqlite");
        });
        var state = (await States(host)).Single();
        Assert.Equal((CliServerPluginStatuses.NeedsConfiguration, "database-sqlite"), (state.Status, state.Settings));
        Assert.Equal(["settings.connectionString"], state.Missing);
        // Its driver serves the stores that set a connection of their own
        var driver = builder!.DatabaseDrivers.Find("sqlite")!;
        Assert.Equal(("database-sqlite", null), (driver.Plugin, driver.ConnectionString));
        Assert.Null(builder.DatabaseDrivers.ConnectionString("sqlite"));
        Assert.Null(builder.DatabaseDrivers.Find("postgres"));
    }

    [Fact]
    public async Task TheDatabasesPlugin_AndTheEfStores_UseTheDriversConnection_UnlessTheySetTheirOwn()
    {
        var dir = TempDir();
        var shared = Path.Combine(dir, "shared.db");
        var own = Path.Combine(dir, "own.db");
        CliBuilder? builder = null;
        var (host, _) = await StartAsync(cli =>
        {
            builder = cli;
            // Added last: the order (Driver) still runs it first
            cli.AddPlugin<AuditEfCorePluginEntry>();
            cli.AddPlugin<JobsPluginEntry>();
            cli.AddPlugin<JobsEfCorePluginEntry>(new { connectionString = own });
            cli.AddPlugin<DatabasesPluginEntry>(new { dialect = "sqlite" });
            cli.AddPlugin<SqliteDriverEntry>(new { connectionString = $"Data Source={shared}" }, "database-sqlite");
        }, dir);
        Assert.All(await States(host), s => Assert.Equal(CliServerPluginStatuses.Loaded, s.Status));
        var providers = host.Services.GetServices<ICliConfigProvider>().Select(p => p.Id).ToList();
        Assert.True(providers.IndexOf("database-sqlite") < providers.IndexOf("audit-efcore"));
        var driver = builder!.DatabaseDrivers.Find("sqlite")!;
        Assert.Equal(("database-sqlite", $"Data Source={shared}"), (driver.Plugin, driver.ConnectionString));

        // The audit store keeps its entries in the driver's database, the jobs store in its own
        var audit = host.Services.GetRequiredKeyedService<IAuditStore>("Qodalis.Cli.Plugin.Audit.InnerStore");
        Assert.IsType<EfCoreAuditStore>(audit);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await host.GetTestClient().GetAsync("/api/v1/qcli/databases")).StatusCode);
        var main = host.Services.GetRequiredService<CliDatabases>().Require("main");
        Assert.NotNull(await main.StatusAsync());
        Assert.True(File.Exists(shared));
        Assert.True(File.Exists(own));
    }

    [Fact]
    public async Task AStoreOfADialectWithADriverPlugin_WithoutAnyConnection_NeedsTheirOne()
    {
        var (host, _) = await StartAsync(cli =>
        {
            cli.AddPlugin<AuditEfCorePluginEntry>(new { dialect = "postgres" });
            cli.AddPlugin<DatabasesPluginEntry>(new { dialect = "mssql" });
            cli.AddPlugin<JobsPluginEntry>();
            cli.AddPlugin<JobsEfCorePluginEntry>();
        });
        var states = await States(host);
        Assert.All(states.Where(s => s.Name != "jobs"), s => Assert.Equal(CliServerPluginStatuses.NeedsConfiguration, s.Status));
        Assert.Equal(["settings.connectionString or the database-postgres plugin's connection"], states.Single(s => s.Name == "audit-efcore").Missing);
        Assert.Equal(["settings.connectionString or the database-mssql plugin's connection"], states.Single(s => s.Name == "databases").Missing);
        Assert.Equal(["settings.connectionString"], states.Single(s => s.Name == "jobs-efcore").Missing);
        Assert.Null(host.Services.GetService<IAuditStore>());
    }

    [Fact]
    public async Task ADriverMissingAnAssembly_Fails_WithIt()
    {
        var (host, _) = await StartAsync(cli => cli.AddPlugin<BrokenDriverEntry>(new { connectionString = "Host=localhost" }, "database-postgres"));
        var state = (await States(host)).Single();
        Assert.Equal(CliServerPluginStatuses.Failed, state.Status);
        Assert.Equal("the postgres driver is incomplete: Acme.Nowhere is missing (reinstall the database-postgres plugin)", state.Error);
    }

    [Fact]
    public async Task APluginAssemblyTheHostOnlyReferences_RoutesNothing()
    {
        // The host references Qodalis.Cli.Plugin.Databases (a dependency of the EF Core stores) without adding the
        // databases plugin: its API is a missing route (404), not a controller without its services (500)
        var (host, _) = await StartAsync(_ => { });
        var client = host.GetTestClient();
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/qcli/databases")).StatusCode);
        var (withDatabases, _) = await StartAsync(cli => cli.AddPlugin<DatabasesPluginEntry>(new { dialect = "sqlite", connectionString = "main.db" }));
        Assert.Equal(System.Net.HttpStatusCode.OK, (await withDatabases.GetTestClient().GetAsync("/api/v1/qcli/databases")).StatusCode);
    }

    [Fact]
    public async Task TheFilesystemApiTurnedOff_AnswersAMissingRoute()
    {
        // 404 without "error": the dashboard's Files page then says the filesystem plugin is not installed
        var (host, _) = await StartAsync(_ => { });
        var res = await host.GetTestClient().GetAsync("/api/qcli/fs/ls?path=/");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, res.StatusCode);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();
        Assert.False(body.ContainsKey("error"));
        Assert.Equal("FS_DISABLED", body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConfigStores_BecomeTheDefaultStore_TheirOwnSettingsStayInTheFile()
    {
        var dir = TempDir();
        var (host, _) = await StartAsync(cli =>
        {
            cli.AddPlugin<ConfigEfCorePluginEntry>(new { connectionString = "config.db" });
            cli.AddPlugin<WeatherPluginEntry>();
        }, dir);
        Assert.IsType<EfCoreConfigStore>(host.Services.GetRequiredService<CliConfigRegistry>().DefaultStore);
        await SetAsync(host, "weather", "defaultLocation", "Rome");
        Assert.False(File.Exists(Path.Combine(dir, CliServerPlugins.SettingsFile)) &&
            File.ReadAllText(Path.Combine(dir, CliServerPlugins.SettingsFile)).Contains("Rome"));
        var (again, _) = await StartAsync(cli =>
        {
            cli.AddPlugin<ConfigEfCorePluginEntry>(new { connectionString = "config.db" });
            cli.AddPlugin<WeatherPluginEntry>();
        }, dir);
        var weather = again.Services.GetServices<ICliConfigProvider>().OfType<CliStoredConfigProvider>().Single(p => p.Id == "weather");
        Assert.Equal("Rome", weather.Text("defaultLocation"));

        var (redis, _) = await StartAsync(cli => cli.AddPlugin<RedisConfigPluginEntry>());
        Assert.IsType<InMemoryConfigStore>(redis.Services.GetRequiredService<CliConfigRegistry>().DefaultStore);
        Assert.Equal(["settings.url"], (await States(redis)).Single().Missing);
    }

    [Fact]
    public async Task Users_ProtectsTheServerFromTheFirstRequest_PublicApiAndPortalNeedWhatTheyBuildOn()
    {
        var (alone, _) = await StartAsync(cli =>
        {
            cli.AddPlugin<PublicApiPluginEntry>();
            cli.AddPlugin<PortalPluginEntry>();
        });
        var states = await States(alone);
        Assert.Equal(["users plugin"], states.Single(s => s.Name == "public-api").Missing);
        Assert.Equal(["docs or public-api plugin"], states.Single(s => s.Name == "portal").Missing);
        Assert.All(states, s => Assert.Equal(CliServerPluginStatuses.NeedsConfiguration, s.Status));

        var (host, _) = await StartAsync(cli =>
        {
            cli.AddPlugin<PortalPluginEntry>();
            cli.AddPlugin<PublicApiPluginEntry>(new { requestsPerDay = 100 });
            cli.AddPlugin<UsersPluginEntry>(new { defaultAdminPassword = "root-password-1" });
            cli.AddPlugin<DocsPluginEntry>();
        });
        states = await States(host);
        Assert.All(states, s => Assert.Equal(CliServerPluginStatuses.Loaded, s.Status));
        var client = host.GetTestClient();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/qcli/commands")).StatusCode);
        Assert.True(host.Services.GetRequiredService<Qodalis.Cli.Security.CliServerOptions>().RequireAuthentication);
        Assert.Equal("users-plugin", states.Single(s => s.Name == "users").Settings);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/qcli/api/openapi.json")).StatusCode);
    }

    [Fact]
    public async Task Databases_FromItsSettings_WithTheDriverOfItsDialect_AndLogsSeqNeedsAUrl()
    {
        var (host, dir) = await StartAsync(cli =>
        {
            cli.AddPlugin<DatabasesPluginEntry>(new { dialect = "sqlite", connectionString = "app.db", migrationsFolder = "migrations" });
            cli.AddPlugin<SeqLogsPluginEntry>();
        });
        var databases = host.Services.GetRequiredService<CliDatabases>();
        var main = (await databases.OverviewAsync()).Single();
        Assert.Equal(("main", "sqlite"), (main.Name, main.Dialect));
        Assert.True(Directory.Exists(Path.Combine(dir, "plugin-data", "databases", "migrations")));
        var states = await States(host);
        Assert.Equal(CliServerPluginStatuses.Loaded, states.Single(s => s.Name == "databases").Status);
        Assert.Equal(["settings.serverUrl"], states.Single(s => s.Name == "logs-seq").Missing);
        Assert.Throws<InvalidOperationException>(() => CliDbConnections.Create("oracle", "x"));
    }

    [Fact]
    public async Task FileSystemEfCore_KeepsFilesInItsDatabase()
    {
        var (host, dir) = await StartAsync(cli => cli.AddPlugin<FileSystemEfCorePluginEntry>(new { connectionString = "files.db", readOnly = false }));
        var files = host.Services.GetRequiredService<IFileStorageProvider>();
        await files.WriteFileAsync("/a.txt", "A");
        Assert.Equal("A", await files.ReadFileAsync("/a.txt"));
        Assert.True(File.Exists(Path.Combine(dir, "plugin-data", "filesystem-efcore", "files.db")));
    }
}
