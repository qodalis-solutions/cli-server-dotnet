using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Config;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Plugin.Config.EfCore;
using Qodalis.Cli.Plugin.Jobs;
using Qodalis.Cli.Plugin.Users;

namespace Qodalis.Cli.Tests.Config;

/// <summary>
/// The built-in config providers and their runtime effect: server, host (secrets masked), cors (live origins), mcp (tool
/// lists, read-only), filesystem, jobs (history retention), logs (buffer), users, custom; persistence through the stores.
/// </summary>
public class ConfigProvidersTests
{
    private static readonly CliConfigContext All = ConfigTestHost.All;

    private static CliConfigUpdateRequest R(string json, string? version = null) =>
        new() { Changes = ConfigStoreContractTests.C(json), Version = version };

    private static CliConfigSectionView Section(CliConfigProviderView view, string id) => view.Sections.Single(s => s.Id == id);

    private static JsonNode? ValueOf(CliConfigSectionView section, string key) => section.Entries.Single(e => e.Key == key).Value;

    private static string Json(JsonNode? node) => node?.ToJsonString() ?? "null";

    // ---------------------------------------------------------------- built-in

    [Fact]
    public async Task BuiltIns_RegisteredOnEveryServer_InOrder_FilesystemOnlyWithAFilesystem()
    {
        await using (var plain = await ConfigTestHost.StartAsync())
            Assert.Equal(["server", "host", "cors", "mcp", "custom"], plain.Registry.Providers.Select(p => p.Id));
        await using var withFs = await ConfigTestHost.StartAsync(cli => cli.AddFileSystem());
        Assert.Contains("filesystem", withFs.Registry.Providers.Select(p => p.Id));
        Assert.Same(withFs.Config, withFs.Service<ICliConfigService>());
    }

    [Fact]
    public async Task AHostProviderWithABuiltInId_ReplacesTheBuiltIn()
    {
        await using var host = await ConfigTestHost.StartAsync(cli => cli.AddConfigProvider(CliCustomConfigProvider.Create(title: "Mine")));
        Assert.Equal("Mine", (await host.Config.GetAsync("custom", All)).Title);
    }

    [Fact]
    public async Task DuplicateHostProviders_FailAtStartup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ConfigTestHost.StartAsync(cli =>
        {
            cli.AddConfigProvider(CliCustomConfigProvider.Create("mine"));
            cli.AddConfigProvider(CliCustomConfigProvider.Create("mine"));
        }));
        Assert.Equal("config provider already registered: mine", ex.Message);
    }

    [Fact]
    public async Task Server_TheProcess_ReadOnly()
    {
        await using var host = await ConfigTestHost.StartAsync();
        var view = await host.Config.GetAsync("server", All);
        Assert.False(view.Writable);
        var runtime = Section(view, "runtime");
        Assert.Equal("dotnet", CliConfigValues.AsString(ValueOf(runtime, "platform")));
        Assert.Equal(Environment.Version.ToString(), CliConfigValues.AsString(ValueOf(runtime, "platformVersion")));
        Assert.Equal(Environment.ProcessId, CliConfigValues.AsNumber(ValueOf(runtime, "pid")));
        Assert.Matches("^\\d+(ms|s|m|h|d)$", CliConfigValues.AsString(ValueOf(runtime, "uptime")));
        Assert.All(runtime.Entries, e => Assert.False(e.Mutable));
    }

    // ---------------------------------------------------------------- host

    [Theory]
    [InlineData("DB_PASSWORD"), InlineData("JWT_SECRET"), InlineData("GITHUB_TOKEN"), InlineData("API_KEY"), InlineData("STRIPE_APIKEY")]
    [InlineData("KEY"), InlineData("AWS_SECRET_ACCESS_KEY"), InlineData("PRIVATE_KEY_PATH"), InlineData("SESSION_COOKIE"), InlineData("SENTRY_DSN")]
    [InlineData("CONNECTION_STRING"), InlineData("QCLI_ADMIN_PASSWORD"), InlineData("BASIC_AUTH"), InlineData("TERM_SESSION_ID")]
    [InlineData("SSH_PASSPHRASE"), InlineData("DB_PASS"), InlineData("MONGO_CONN"), InlineData("REDIS_CONNSTR"), InlineData("GPG_KEYRING")]
    [InlineData("ssh_auth_sock"), InlineData("Oauth_Client"), InlineData("X_CREDENTIALS")]
    [InlineData("ConnectionStrings:Default"), InlineData("Jwt:Key"), InlineData("Authentication:Google:ClientSecret")]
    public void Host_SecretNames(string name) => Assert.True(CliHostConfigProvider.IsSecret(name, "x"), name);

    [Theory]
    [InlineData("PATH"), InlineData("HOME"), InlineData("ASPNETCORE_ENVIRONMENT"), InlineData("PORT"), InlineData("PWD"), InlineData("HOST")]
    [InlineData("TZ"), InlineData("LANG"), InlineData("QCLI_PORT"), InlineData("Logging:LogLevel:Default"), InlineData("AllowedHosts")]
    public void Host_HarmlessNames(string name) => Assert.False(CliHostConfigProvider.IsSecret(name, "x"), name);

    [Fact]
    public void Host_SecretValues()
    {
        Assert.True(CliHostConfigProvider.IsSecret("DATABASE_URL", "postgres://user:pw@db:5432/app"));
        Assert.False(CliHostConfigProvider.IsSecret("DATABASE_URL", "postgres://db:5432/app"));
        Assert.True(CliHostConfigProvider.IsSecret("UPSTREAM", "https://admin:hunter2@example.com/x"));
        Assert.False(CliHostConfigProvider.IsSecret("UPSTREAM", "https://user@example.com/x"));
        Assert.True(CliHostConfigProvider.IsSecret("Db", "Server=db;Database=app;User Id=sa;Password=hunter2"));
        Assert.True(CliHostConfigProvider.IsSecret("MY_THING", "x", ["MY_*"]));
    }

    private static async Task<List<CliConfigEntry>> EnvironmentOf(CliHostEnvironmentOptions options, Dictionary<string, string?> env) =>
        (await new CliHostConfigProvider(() => [], null, options, () => env).GetSectionsAsync(All))[1].Entries;

    [Fact]
    public async Task Host_ShowsOnlyQcliAndAFewHarmlessVariablesByDefault_AllowAddsNames_SecretsStillMasked()
    {
        Assert.Contains("QCLI_*", CliHostEnvironmentOptions.DefaultAllow);
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin", ["HOME"] = "/home/x", ["TERM_SESSION_ID"] = "abc", ["SSH_AUTH_SOCK"] = "/tmp/ssh", ["AWS_ACCESS_KEY_ID"] = "AKIA",
            ["ASPNETCORE_ENVIRONMENT"] = "Production", ["PORT"] = "8046", ["HOST"] = "0.0.0.0", ["TZ"] = "UTC", ["LANG"] = "en_US.UTF-8",
            ["QCLI_URL"] = "https://a:b@x.io", ["QCLI_MODE"] = "demo", ["APP_REGION"] = "eu",
        };
        Assert.Equal(["ASPNETCORE_ENVIRONMENT", "HOST", "LANG", "PORT", "QCLI_MODE", "QCLI_URL", "TZ"],
            (await EnvironmentOf(new(), env)).Select(e => e.Key));
        Assert.Equal(["APP_REGION", "ASPNETCORE_ENVIRONMENT", "HOST", "LANG", "PORT", "QCLI_MODE", "QCLI_URL", "TERM_SESSION_ID", "TZ"],
            (await EnvironmentOf(new() { Allow = ["APP_*", "TERM_SESSION_ID"] }, env)).Select(e => e.Key));
        var entries = await EnvironmentOf(new() { Allow = ["TERM_SESSION_ID"] }, env);
        var session = entries.Single(e => e.Key == "TERM_SESSION_ID");
        Assert.Equal(("secret", null, true), (session.Type, session.Value, session.IsSet));
        Assert.Equal(("secret", null), (entries.Single(e => e.Key == "QCLI_URL").Type, entries.Single(e => e.Key == "QCLI_URL").Value));
        Assert.Equal("demo", CliConfigValues.AsString(entries.Single(e => e.Key == "QCLI_MODE").Value));
        var views = JsonSerializer.Serialize(entries.Select(e => CliConfigViews.Entry(e, false)));
        Assert.DoesNotContain("/usr/bin", views);
        Assert.DoesNotContain("AKIA", views);
        Assert.DoesNotContain("a:b@", views);
    }

    [Fact]
    public async Task Host_IncludeReplacesTheList_SortsAndMasks_ExcludeAndSecrets()
    {
        var env = new Dictionary<string, string?>
        {
            ["ZED"] = "z", ["ALPHA"] = "a", ["DB_PASSWORD"] = "hunter2", ["EMPTY_TOKEN"] = "", ["DATABASE_URL"] = "mysql://u:p@h/db", ["QCLI_PORT"] = "8047",
        };
        var host = new CliHostConfigProvider(() => [new CliHostOption("basePath", null, null, "/api/qcli")], null,
            new CliHostEnvironmentOptions { Include = ["*"] }, () => env);
        var sections = await host.GetSectionsAsync(All);
        var option = Assert.Single(sections[0].Entries);
        Assert.Equal(("basePath", "string", "/api/qcli"), (option.Key, option.Type, CliConfigValues.AsString(option.Value)));
        var environment = sections[1].Entries;
        Assert.Equal(["ALPHA", "DATABASE_URL", "DB_PASSWORD", "EMPTY_TOKEN", "QCLI_PORT", "ZED"], environment.Select(e => e.Key));
        var password = environment.Single(e => e.Key == "DB_PASSWORD");
        Assert.Equal(("secret", null, true, true, "environment"), (password.Type, password.Value, password.IsSet, password.Sensitive, password.Source));
        Assert.False(environment.Single(e => e.Key == "EMPTY_TOKEN").IsSet);
        Assert.Equal("secret", environment.Single(e => e.Key == "DATABASE_URL").Type);
        var alpha = environment.Single(e => e.Key == "ALPHA");
        Assert.Equal(("string", "a", "environment"), (alpha.Type, CliConfigValues.AsString(alpha.Value), alpha.Source));
        var only = new CliHostConfigProvider(() => [], null,
            new CliHostEnvironmentOptions { Include = ["QCLI_*", "ZED"], Exclude = ["ZED"], Secrets = ["QCLI_PORT"] }, () => env);
        var port = Assert.Single((await only.GetSectionsAsync(All))[1].Entries);
        Assert.Equal(("QCLI_PORT", "secret", true), (port.Key, port.Type, port.IsSet));
    }

    [Fact]
    public async Task Host_Configuration_AllowedKeysOnly_ConnectionStringsMasked_EnvironmentKeysLeftToTheirSection()
    {
        var root = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Qcli:Mode"] = "demo",
                ["Logging:LogLevel:Default"] = "Information",
                ["ConnectionStrings:Default"] = "Server=db;Password=hunter2",
                ["Payments:ApiKey"] = "sk_live",
                ["Payments:Region"] = "eu",
            })
            .Build();
        var host = new CliHostConfigProvider(() => [], root, new CliHostEnvironmentOptions { Allow = ["ConnectionStrings:*", "Payments:*"] },
            () => new Dictionary<string, string?>());
        var configuration = (await host.GetSectionsAsync(All))[2];
        Assert.Equal("configuration", configuration.Id);
        Assert.Equal(["ConnectionStrings:Default", "Logging:LogLevel:Default", "Payments:ApiKey", "Payments:Region", "Qcli:Mode"],
            configuration.Entries.Select(e => e.Key));
        Assert.True(configuration.Entries.Single(e => e.Key == "ConnectionStrings:Default").Sensitive);
        Assert.True(configuration.Entries.Single(e => e.Key == "Payments:ApiKey").Sensitive);
        Assert.Equal("eu", CliConfigValues.AsString(configuration.Entries.Single(e => e.Key == "Payments:Region").Value));
        var json = JsonSerializer.Serialize(configuration.Entries.Select(e => CliConfigViews.Entry(e, false)));
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("sk_live", json);
        var hidden = new CliHostConfigProvider(() => [], root, null, () => new Dictionary<string, string?>());
        Assert.Equal(["Logging:LogLevel:Default", "Qcli:Mode"], (await hidden.GetSectionsAsync(All))[2].Entries.Select(e => e.Key));
    }

    [Fact]
    public async Task Host_ThroughTheServer_OptionsTyped_SecretsNeverInTheResponse()
    {
        Environment.SetEnvironmentVariable("QCLI_TEST_SECRET_TOKEN", "do-not-show-me");
        try
        {
            await using var host = await ConfigTestHost.StartAsync(cli => cli.ConfigureConfig(o => o.Environment.Include = ["QCLI_TEST_*"]));
            var view = await host.Config.GetAsync("host", All);
            var options = Section(view, "options");
            var rows = options.Entries.Single(e => e.Key == "maxTableRows");
            Assert.Equal(("number", 200.0), (rows.Type, CliConfigValues.AsNumber(rows.Value)));
            var shell = options.Entries.Single(e => e.Key == "enableShell");
            Assert.Equal(("boolean", false), (shell.Type, CliConfigValues.AsBoolean(shell.Value)));
            var secret = Assert.Single(Section(view, "environment").Entries);
            Assert.Equal(("QCLI_TEST_SECRET_TOKEN", null, true, true), (secret.Key, secret.Value, secret.IsSet, secret.Sensitive));
            Assert.DoesNotContain("do-not-show-me", JsonSerializer.Serialize(view));
        }
        finally
        {
            Environment.SetEnvironmentVariable("QCLI_TEST_SECRET_TOKEN", null);
        }
    }

    // ---------------------------------------------------------------- cors

    private const string NewOrigin = "https://new.example";

    [Fact]
    public async Task Cors_ANewOriginGetsCorsHeadersRightAway_ResetRestoresTheStartList()
    {
        await using var host = await ConfigTestHost.StartAsync(cli => cli.SetAllowedOrigins("https://old.example"));
        var cors = Section(await host.Config.GetAsync("cors", All), "cors");
        Assert.Equal("origins", CliConfigValues.AsString(ValueOf(cors, "mode")));
        Assert.Equal("[\"https://old.example\"]", Json(ValueOf(cors, "allowedOrigins")));
        var entry = cors.Entries.Single(e => e.Key == "allowedOrigins");
        Assert.Equal(("string[]", "origins", true), (entry.Type, entry.Editor, entry.Mutable));

        Assert.Null(ConfigTestHost.AllowOrigin(await host.GetWithOriginAsync("/api/qcli/versions", NewOrigin)));
        await host.Config.UpdateAsync("cors", "cors", R($"{{\"allowedOrigins\":[\"https://old.example\",\"{NewOrigin}\"]}}"), All);
        Assert.Equal(NewOrigin, ConfigTestHost.AllowOrigin(await host.GetWithOriginAsync("/api/qcli/versions", NewOrigin)));
        await host.Config.UpdateAsync("cors", "cors", R("{\"allowedOrigins\":null}"), All);
        Assert.Null(ConfigTestHost.AllowOrigin(await host.GetWithOriginAsync("/api/qcli/versions", NewOrigin)));
        Assert.Equal(["https://old.example"], host.Service<Qodalis.Cli.Security.CliServerOptions>().AllowedOrigins);
    }

    [Fact]
    public async Task Cors_AServerStartedWithoutOriginsCanAddSome()
    {
        await using var host = await ConfigTestHost.StartAsync();
        Assert.Null(ConfigTestHost.AllowOrigin(await host.GetWithOriginAsync("/api/qcli/versions", NewOrigin)));
        await host.Config.UpdateAsync("cors", "cors", R($"{{\"allowedOrigins\":[\"{NewOrigin}\"]}}"), All);
        Assert.Equal(NewOrigin, ConfigTestHost.AllowOrigin(await host.GetWithOriginAsync("/api/qcli/versions", NewOrigin)));
    }

    [Fact]
    public async Task Cors_TheMcpEndpointFollowsTheLiveList()
    {
        await using var host = await ConfigTestHost.StartAsync(cli => cli.AddMcp());
        var ping = Mcp.Request("ping");
        Assert.Equal(403, (int)(await Mcp.PostAsync(host.Client, ping, origin: NewOrigin)).StatusCode);
        await host.Config.UpdateAsync("cors", "cors", R($"{{\"allowedOrigins\":[\"{NewOrigin}\"]}}"), All);
        Assert.Equal(200, (int)(await Mcp.PostAsync(host.Client, ping, origin: NewOrigin)).StatusCode);
    }

    [Fact]
    public async Task Cors_ValidatesEachOrigin_TheModeIsReadOnly()
    {
        await using var host = await ConfigTestHost.StartAsync();
        foreach (var bad in new[] { "example.com", "https://a.example/path", "ftp://a.example", "*", "https://a.example:99999" })
        {
            var ex = await Assert.ThrowsAsync<CliConfigException>(() => host.Config.UpdateAsync("cors", "cors", R($"{{\"allowedOrigins\":[\"{bad}\"]}}"), All));
            Assert.Equal(400, ex.Status);
            Assert.Contains("is not an origin", ex.Errors!["allowedOrigins"]);
        }
        await host.Config.UpdateAsync("cors", "cors", R("{\"allowedOrigins\":[\"http://localhost:*\",\"https://a.example:8443\",\"http://h/\"]}"), All);
        var mode = await Assert.ThrowsAsync<CliConfigException>(() => host.Config.UpdateAsync("cors", "cors", R("{\"mode\":\"permissive\"}"), All));
        Assert.Equal("read-only", mode.Errors!["mode"]);
    }

    // ---------------------------------------------------------------- mcp

    [Fact]
    public async Task Mcp_ReadOnlyHidesConfigSet_ACallIsRefused()
    {
        await using var host = await ConfigTestHost.StartAsync(cli => cli.AddMcp());
        Assert.Superset(new HashSet<string> { "config_list", "config_get", "config_set" }, (await Mcp.ToolNamesAsync(host.Client)).ToHashSet());
        await host.Config.UpdateAsync("mcp", "mcp", R("{\"readOnly\":true}"), All);
        var listed = await Mcp.ToolNamesAsync(host.Client);
        Assert.Superset(new HashSet<string> { "config_list", "config_get", "server_info" }, listed.ToHashSet());
        Assert.DoesNotContain("config_set", listed);
        var (code, message) = await Mcp.ErrorAsync(host.Client, "tools/call",
            new { name = "config_set", arguments = new { provider = "custom", section = "custom", changes = new { a = 1 } } });
        Assert.Equal((-32602, "Tool config_set is not available: the MCP endpoint is read-only"), (code, message));
        Assert.Empty((await host.Config.GetAsync("custom", All)).Sections[0].Entries);
        await host.Config.UpdateAsync("mcp", "mcp", R("{\"readOnly\":null}"), All);
        Assert.Contains("config_set", await Mcp.ToolNamesAsync(host.Client));
    }

    [Fact]
    public async Task Mcp_AllowListAndDenyList_EmptyAllowsEveryTool()
    {
        await using var host = await ConfigTestHost.StartAsync(cli => cli.AddMcp(o => o.Exclude = ["server_info"]));
        Assert.DoesNotContain("server_info", await Mcp.ToolNamesAsync(host.Client));
        await host.Config.UpdateAsync("mcp", "mcp", R("{\"tools\":[\"config_*\"],\"exclude\":[]}"), All);
        Assert.Equal(["config_get", "config_list", "config_set"], (await Mcp.ToolNamesAsync(host.Client)).Order());
        await host.Config.UpdateAsync("mcp", "mcp", R("{\"tools\":[]}"), All);
        Assert.Contains("server_info", await Mcp.ToolNamesAsync(host.Client));
        await host.Config.UpdateAsync("mcp", "mcp", R("{\"exclude\":null}"), All);
        Assert.DoesNotContain("server_info", await Mcp.ToolNamesAsync(host.Client));
    }

    [Fact]
    public async Task Mcp_InstructionsAreReturnedByInitialize()
    {
        await using var host = await ConfigTestHost.StartAsync(cli => cli.AddMcp(o => o.Instructions = "first"));
        async Task<string?> Init()
        {
            var result = await Mcp.ResultAsync(host.Client, "initialize", new { protocolVersion = "2025-06-18" });
            return result.TryGetProperty("instructions", out var i) ? i.GetString() : null;
        }
        Assert.Equal("first", await Init());
        await host.Config.UpdateAsync("mcp", "mcp", R("{\"instructions\":\"second\"}"), All);
        Assert.Equal("second", await Init());
        await host.Config.UpdateAsync("mcp", "mcp", R("{\"instructions\":\"\"}"), All);
        Assert.Null(await Init());
    }

    [Fact]
    public async Task Mcp_ValidatesToolPatterns_ReadOnlyWhenMcpIsOff()
    {
        await using (var on = await ConfigTestHost.StartAsync(cli => cli.AddMcp()))
            Assert.Equal(400, (await Assert.ThrowsAsync<CliConfigException>(() => on.Config.UpdateAsync("mcp", "mcp", R("{\"tools\":[\"Bad Name\"]}"), All))).Status);
        await using var off = await ConfigTestHost.StartAsync();
        var view = Section(await off.Config.GetAsync("mcp", All), "mcp");
        Assert.False(CliConfigValues.AsBoolean(ValueOf(view, "enabled")));
        Assert.False(view.Mutable);
        Assert.Equal(400, (await Assert.ThrowsAsync<CliConfigException>(() => off.Config.UpdateAsync("mcp", "mcp", R("{\"readOnly\":true}"), All))).Status);
    }

    // ---------------------------------------------------------------- filesystem

    [Fact]
    public async Task Filesystem_StorageAndRootsAreReadOnly()
    {
        var root = Directory.CreateTempSubdirectory("qcli-config-fs-").FullName;
        try
        {
            await using var host = await ConfigTestHost.StartAsync(cli => cli.AddFileSystem(o => o.AllowedPaths.Add(root)));
            var view = await host.Config.GetAsync("filesystem", All);
            var fs = Section(view, "filesystem");
            Assert.Equal((false, null), (fs.Mutable, fs.Version));
            Assert.Equal($"[{JsonSerializer.Serialize(root)}]", Json(ValueOf(fs, "allowedPaths")));
            Assert.All(fs.Entries, e => Assert.False(e.Mutable));
            var ex = await Assert.ThrowsAsync<CliConfigException>(() => host.Config.UpdateAsync("filesystem", "filesystem", R("{\"allowedPaths\":[\"/\"]}"), All));
            Assert.Equal(400, ex.Status);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ---------------------------------------------------------------- jobs

    [Fact]
    public async Task Jobs_ChangesTheScheduler_RegistersThePruneJobWhenNeeded_ValidatesDurations()
    {
        await using var host = await ConfigTestHost.StartAsync(cli => cli.ConfigureJobs(_ => { }));
        var scheduler = host.Service<CliJobScheduler>();
        var view = Section(await host.Config.GetAsync("jobs", All), "history");
        Assert.Null(ValueOf(view, "maxAge"));
        Assert.Equal("1d", CliConfigValues.AsString(ValueOf(view, "pruneInterval")));
        Assert.False(scheduler.History.HasLimits);
        Assert.False(scheduler.Jobs.ContainsKey(PruneHistoryJob.JobId));
        await host.Config.UpdateAsync("jobs", "history", R("{\"maxAge\":\"30d\",\"maxRunsPerJob\":100}"), All);
        Assert.Equal((TimeSpan.FromDays(30), 100), (scheduler.History.MaxAge, scheduler.History.MaxRunsPerJob));
        Assert.True(scheduler.Jobs.ContainsKey(PruneHistoryJob.JobId));
        Assert.Equal(400, (await Assert.ThrowsAsync<CliConfigException>(() => host.Config.UpdateAsync("jobs", "history", R("{\"maxAge\":\"500ms\"}"), All))).Status);
        Assert.Equal("must be at least 1", (await Assert.ThrowsAsync<CliConfigException>(() =>
            host.Config.UpdateAsync("jobs", "history", R("{\"maxRunsPerJob\":0}"), All))).Errors!["maxRunsPerJob"]);
        Assert.Equal("read-only", (await Assert.ThrowsAsync<CliConfigException>(() =>
            host.Config.UpdateAsync("jobs", "history", R("{\"pruneInterval\":\"1h\"}"), All))).Errors!["pruneInterval"]);
        await host.Config.UpdateAsync("jobs", "history", R("{\"maxAge\":null}"), All);
        Assert.Equal((null, 100), (scheduler.History.MaxAge, scheduler.History.MaxRunsPerJob));
    }

    [Fact]
    public async Task Jobs_DefaultsComeFromThePluginOptions()
    {
        await using var host = await ConfigTestHost.StartAsync(cli => cli.ConfigureJobs(o =>
        {
            o.History.MaxAge = TimeSpan.FromDays(7);
            o.History.FailedMaxRunsPerJob = 5;
            o.History.PruneInterval = TimeSpan.FromHours(6);
        }));
        var view = Section(await host.Config.GetAsync("jobs", All), "history");
        Assert.Equal("7d", CliConfigValues.AsString(ValueOf(view, "maxAge")));
        Assert.Equal(5, CliConfigValues.AsNumber(ValueOf(view, "failedMaxRunsPerJob")));
        Assert.Equal("6h", CliConfigValues.AsString(ValueOf(view, "pruneInterval")));
    }

    // ---------------------------------------------------------------- logs

    private static void Push(LogRingBuffer buffer, string message, string level = "INFO") =>
        buffer.Add(new LogEntry { Level = level, Message = message, Source = "t" });

    [Fact]
    public async Task Logs_CapacityAndLowestLevelChangeTheBuffer()
    {
        var buffer = new LogRingBuffer(150);
        var provider = AdminConfigProviders.Logs(buffer);
        for (var i = 0; i < 150; i++) Push(buffer, $"m{i}");
        await provider.UpdateAsync("buffer", ConfigStoreContractTests.C("{\"capacity\":100}"), null, All);
        Assert.Equal(100, buffer.Capacity);
        var kept = buffer.Query(limit: 1000).Items;
        Assert.Equal(100, kept.Count);
        Assert.Equal(("m149", "m50"), (kept[0].Message, kept[^1].Message));
        Push(buffer, "next");
        Assert.Equal("next", buffer.Query(limit: 1000).Items[0].Message);
        await provider.UpdateAsync("buffer", ConfigStoreContractTests.C("{\"minLevel\":\"WARN\"}"), null, All);
        Push(buffer, "dropped");
        Push(buffer, "kept", "ERROR");
        var messages = buffer.Query(limit: 1000).Items.Select(e => e.Message).ToList();
        Assert.DoesNotContain("dropped", messages);
        Assert.Equal("kept", messages[0]);
        Assert.Equal(new Dictionary<string, string> { ["capacity"] = "must be a whole number" },
            await provider.ValidateAsync("buffer", ConfigStoreContractTests.C("{\"capacity\":150.5}"), All));
        await provider.UpdateAsync("buffer", ConfigStoreContractTests.C("{\"capacity\":null,\"minLevel\":null}"), null, All);
        Assert.Equal((150, "DEBUG"), (buffer.Capacity, buffer.MinLevel));
    }

    [Fact]
    public void Logs_ResizeGrowsAndKeepsOrder_InvalidSizesAndLevelsThrow()
    {
        var buffer = new LogRingBuffer(3);
        foreach (var m in new[] { "a", "b", "c", "d" }) Push(buffer, m);
        buffer.Resize(5);
        Push(buffer, "e");
        Assert.Equal(["e", "d", "c", "b"], buffer.Query(limit: 10).Items.Select(e => e.Message));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Resize(0));
        Assert.Throws<ArgumentException>(() => buffer.MinLevel = "TRACE");
    }

    // ---------------------------------------------------------------- users

    [Fact]
    public async Task Users_SessionsLockoutAndPasswordsChangeTheOptions_AccessIsReadOnly()
    {
        var options = new UsersModuleOptions();
        var provider = UsersConfigProvider.Create(options);
        var sections = await provider.GetSectionsAsync(All);
        Assert.Equal(["sessions", "security", "access"], sections.Select(s => s.Id));
        Assert.Equal("30m", CliConfigValues.AsString(sections[0].Entries.Single(e => e.Key == "sessionTimeout").Value));
        await provider.UpdateAsync("sessions", ConfigStoreContractTests.C("{\"sessionTimeout\":\"2h\",\"maxSessionLifetime\":\"1d\"}"), null, All);
        Assert.Equal((7_200_000L, 86_400_000L), (options.EffectiveSessionTimeoutMs, options.EffectiveMaxSessionLifetimeMs));
        await provider.UpdateAsync("security", ConfigStoreContractTests.C("{\"maxLoginAttempts\":3,\"lockout\":\"10m\",\"minPasswordLength\":12}"), null, All);
        Assert.Equal((3, 600_000L, 12), (options.MaxLoginAttempts, options.LockoutMs, options.MinPasswordLength));
        await provider.UpdateAsync("security", ConfigStoreContractTests.C("{\"minPasswordLength\":null}"), null, All);
        Assert.Equal(8, options.MinPasswordLength);
        Assert.Equal("read-only", (await provider.ValidateAsync("access", ConfigStoreContractTests.C("{\"protectServer\":false}"), All))["protectServer"]);
        Assert.Null(sections[2].Version);
        Assert.Equal("users", CliConfigValues.AsString(sections[2].Entries.Single(e => e.Key == "signIn").Value));
    }

    [Fact]
    public async Task Users_ThroughTheServer_ValidatesRanges()
    {
        var registry = new CliConfigRegistry();
        registry.Add(UsersConfigProvider.Create(new UsersModuleOptions()));
        var service = new CliConfigService(registry);
        var ex = await Assert.ThrowsAsync<CliConfigException>(() => service.UpdateAsync("users", "sessions", R("{\"sessionTimeout\":\"30s\"}"), All));
        Assert.Equal("must be at least 1m", ex.Errors!["sessionTimeout"]);
        var whole = await Assert.ThrowsAsync<CliConfigException>(() => service.UpdateAsync("users", "security", R("{\"maxLoginAttempts\":2.5}"), All));
        Assert.Equal("must be a whole number", whole.Errors!["maxLoginAttempts"]);
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public async Task JsonFileStore_AChangeSurvivesARestart_AndIsAppliedAtStartup()
    {
        var dir = Directory.CreateTempSubdirectory("qcli-config-providers-").FullName;
        var file = Path.Combine(dir, "config.json");
        try
        {
            await using (var first = await ConfigTestHost.StartAsync(cli => cli.SetConfigStore(new JsonFileConfigStore(file))))
            {
                await first.Config.UpdateAsync("cors", "cors", R("{\"allowedOrigins\":[\"https://kept.example\"]}"), All);
                await first.Config.UpdateAsync("custom", "custom", R("{\"motd\":\"hello\"}"), All);
            }
            await using var second = await ConfigTestHost.StartAsync(cli => cli.SetConfigStore(new JsonFileConfigStore(file)));
            Assert.Equal("https://kept.example", ConfigTestHost.AllowOrigin(await second.GetWithOriginAsync("/api/qcli/versions", "https://kept.example")));
            var cors = Section(await second.Config.GetAsync("cors", All), "cors");
            var origins = cors.Entries.Single(e => e.Key == "allowedOrigins");
            Assert.Equal(("[\"https://kept.example\"]", "file", "1"), (Json(origins.Value), origins.Source, cors.Version));
            Assert.Equal("hello", CliConfigValues.AsString(ValueOf(Section(await second.Config.GetAsync("custom", All), "custom"), "motd")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DatabaseStore_AppliedAtStartup_PluginsIncluded()
    {
        var file = Path.Combine(Path.GetTempPath(), $"qcli-config-{Guid.NewGuid():N}.db");
        try
        {
            var seed = new EfCoreConfigStore(new SqliteContextFactory<ConfigStorageDbContext>(
                new DbContextOptionsBuilder<ConfigStorageDbContext>().UseSqlite($"Data Source={file}").Options, o => new ConfigStorageDbContext(o)));
            await seed.WriteAsync("jobs", "history", ConfigStoreContractTests.C("{\"maxRunsPerJob\":7}"));
            await seed.WriteAsync("mcp", "mcp", ConfigStoreContractTests.C("{\"readOnly\":true}"));
            await using var host = await ConfigTestHost.StartAsync(cli =>
            {
                cli.AddMcp();
                cli.ConfigureJobs(_ => { });
                cli.AddEfCoreConfigStore(o => o.UseSqlite($"Data Source={file}"));
            });
            Assert.Equal(7, host.Service<CliJobScheduler>().History.MaxRunsPerJob);
            Assert.DoesNotContain("config_set", await Mcp.ToolNamesAsync(host.Client));
            var entry = Section(await host.Config.GetAsync("jobs", All), "history").Entries.Single(e => e.Key == "maxRunsPerJob");
            Assert.Equal((7.0, "database"), (CliConfigValues.AsNumber(entry.Value), entry.Source));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }
}
