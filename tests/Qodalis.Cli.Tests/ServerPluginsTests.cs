using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Config;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugins;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests;

/// <summary>A plugin entry of the tests' own, for plugins added in code.</summary>
public sealed class CodeGreeterEntry : ICliPluginEntry
{
    public static bool Fail;

    public void Register(CliPluginContext context)
    {
        var settings = context.Settings(new CliPluginSettingsOptions
        {
            Title = "Greeter",
            Settings =
            [
                new CliConfigSettingDefinition { Key = "greeting", Type = CliConfigEntryTypes.String, Default = "Hi" },
                new CliConfigSettingDefinition { Key = "token", Type = CliConfigEntryTypes.Secret, Required = true },
                new CliConfigSettingDefinition { Key = "name", Type = CliConfigEntryTypes.String, Required = true },
            ],
        });
        context.Services.AddSingleton(new CodeGreeterContext(context.Source, context.Options.ToJsonString()));
        if (Fail) throw new InvalidOperationException("greeter asked to fail");
        context.Builder.AddProcessor(new CodeGreeterProcessor(() => $"{settings.GetValue("settings", "greeting")} {settings.GetValue("settings", "name")}"));
    }
}

public sealed record CodeGreeterContext(string Source, string Options);

public sealed class CodeGreeterProcessor(Func<string> text) : CliCommandProcessor
{
    public override string Command { get; set; } = "code-greeter";
    public override string Description { get; set; } = "Greets";

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) => Task.FromResult(text());
}

/// <summary>Plugins installed at runtime (installed.json, the fixture assembly through its own context) and added in code.</summary>
[Collection("ServerPlugins")]
public class ServerPluginsTests : IAsyncLifetime
{
    public const string FixturePackage = "Qodalis.Cli.Plugin.TestFixture";

    public static string FixtureDll => Path.Combine(AppContext.BaseDirectory, "server-plugin-fixture", $"{FixturePackage}.dll");

    private readonly List<string> _dirs = [];
    private readonly List<IHost> _hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        CodeGreeterEntry.Fail = false;
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
        var dir = Path.Combine(Path.GetTempPath(), $"qcli-server-plugins-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    /// <summary>Installs the fixture as the marketplace would: its assembly in server-plugins/&lt;package&gt;/&lt;version&gt;, a record.</summary>
    public static void InstallFixture(string dataDir, string name = "fixture", string package = FixturePackage, string version = "1.0.0",
        bool copy = true, IEnumerable<CliServerPluginRecord>? more = null)
    {
        var plugins = new CliServerPlugins(new CliServerPluginsOptions { DataDir = dataDir });
        var dir = plugins.VersionDir(package, version);
        if (copy)
        {
            Directory.CreateDirectory(dir);
            File.Copy(FixtureDll, Path.Combine(dir, $"{package}.dll"), overwrite: true);
        }
        plugins.Save([.. more ?? [], Record(name, package, version, Directory.Exists(dir) ? CliServerPlugins.FileManifest(dir) : null)]);
    }

    public static CliServerPluginRecord Record(string name, string package, string version, IReadOnlyDictionary<string, string>? files = null) => new()
    {
        Name = name, Package = package, Version = version, Integrity = "sha512-AAAA", Title = "Fixture", Files = files,
        InstalledAt = "2026-11-26T10:00:00.000Z", InstalledBy = "admin",
    };

    private async Task<IHost> StartAsync(string dataDir, Action<CliBuilder>? configure = null)
    {
        var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(s => s.AddControllers().AddCli(cli =>
            {
                cli.ConfigureServerPlugins(o => o.DataDir = dataDir);
                configure?.Invoke(cli);
            }));
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(e => e.MapControllers());
                app.UseCli();
            });
        }).StartAsync();
        _hosts.Add(host);
        return host;
    }

    private static ICliCommandProcessor? Processor(IHost host, string name) =>
        host.Services.GetRequiredService<ICliCommandRegistry>().FindProcessor(name);

    private static async Task<CliServerPlugins> Checked(IHost host)
    {
        var plugins = host.Services.GetRequiredService<CliServerPlugins>();
        await plugins.CheckSettingsAsync();
        return plugins;
    }

    private static Task SetAsync(IHost host, string provider, string key, JsonNode? value) =>
        host.Services.GetRequiredService<ICliConfigService>().UpdateAsync(provider, "settings",
            new CliConfigUpdateRequest { Changes = new Dictionary<string, JsonNode?> { [key] = value } }, new CliConfigContext(null, _ => true));

    // ---------------------------------------------------------------- installed at runtime

    [Fact]
    public void TheFixture_IsBuiltNextToTheTests_AndNotLoadedByThem()
    {
        Assert.True(File.Exists(FixtureDll));
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, a => a.GetName().Name == FixturePackage);
        Assert.False(CliPluginLoadContext.HostHas(FixturePackage));
        Assert.True(CliPluginLoadContext.HostHas("Qodalis.Cli.Abstractions"));
    }

    [Fact]
    public void FindAssembly_TakesTheCopyOfTheCallersLoadContext_NeverAnotherPlugins()
    {
        var other = TempDir();
        var mine = TempDir();
        File.Copy(FixtureDll, Path.Combine(other, $"{FixturePackage}.dll"));
        File.Copy(FixtureDll, Path.Combine(mine, $"{FixturePackage}.dll"));
        var otherContext = new CliPluginLoadContext("other", other);
        var mineContext = new CliPluginLoadContext("mine", mine);
        try
        {
            // Another plugin loaded its copy first: the AppDomain lists it
            var theirs = otherContext.LoadFromAssemblyPath(Path.Combine(other, $"{FixturePackage}.dll"));
            Assert.Contains(AppDomain.CurrentDomain.GetAssemblies(), a => a == theirs);

            var found = CliPluginLoadContext.FindAssembly(FixturePackage, mineContext);
            Assert.NotNull(found);
            Assert.NotSame(theirs, found);
            Assert.Same(mineContext, AssemblyLoadContext.GetLoadContext(found!));
            // Asked again (now loaded), the same copy; asked by code of that copy, too
            Assert.Same(found, CliPluginLoadContext.FindAssembly(FixturePackage, mineContext));
            Assert.Same(found, CliPluginLoadContext.FindAssembly(FixturePackage, found!));
            // The host's own assemblies come from the host; an unknown one is null
            Assert.Same(typeof(CliServerPlugins).Assembly, CliPluginLoadContext.FindAssembly("Qodalis.Cli", mineContext));
            Assert.Null(CliPluginLoadContext.FindAssembly("Acme.Nowhere", mineContext));
        }
        finally
        {
            otherContext.Unload();
            mineContext.Unload();
        }
    }

    [Fact]
    public async Task AnInstalledPlugin_LoadsThroughItsOwnContext_WithTheHostsContracts_AndRunsItsCommand()
    {
        var dir = TempDir();
        InstallFixture(dir);
        var host = await StartAsync(dir);

        var processor = Processor(host, "fixture-hello");
        Assert.NotNull(processor);
        var assembly = processor!.GetType().Assembly;
        var context = Assert.IsType<CliPluginLoadContext>(AssemblyLoadContext.GetLoadContext(assembly));
        Assert.True(context.IsCollectible);
        Assert.Equal("qcli-plugin:fixture", context.Name);
        // One copy of the contracts: the plugin's base class is the host's
        Assert.Same(typeof(CliCommandProcessor), processor.GetType().BaseType);
        Assert.Equal("Hello from qcli-plugin:fixture", await processor.HandleAsync(new CliProcessCommand { Command = "fixture-hello" }));

        var installed = host.Services.GetRequiredService<CliInstalledPlugins>().All.Single(p => p.Id == FixturePackage);
        Assert.Equal((CliInstalledPlugin.MarketplaceSource, "1.0.0", "Fixture"), (installed.Source, installed.Version, installed.Name));
        // Its data directory, created
        Assert.True(Directory.Exists(Path.Combine(dir, "plugin-data", "fixture")));
    }

    [Fact]
    public async Task ARequiredSettingWithoutAValue_IsNeedsConfiguration_WithTheMissingKeys_ThenLoadedOnceSet()
    {
        var dir = TempDir();
        InstallFixture(dir);
        var host = await StartAsync(dir);
        var plugins = await Checked(host);
        var state = plugins.States().Single();
        Assert.Equal((CliServerPluginStatuses.NeedsConfiguration, CliServerPluginSources.Marketplace), (state.Status, state.Source));
        Assert.Equal(["settings.apiKey"], state.Missing);
        // Its settings are there to be filled in: required, the secret hidden
        var view = await host.Services.GetRequiredService<ICliConfigService>().GetAsync("fixture", new CliConfigContext(null, _ => true));
        var apiKey = view.Sections.Single().Entries.Single(e => e.Key == "apiKey");
        Assert.True(apiKey.Required);
        Assert.False(apiKey.IsSet);

        await SetAsync(host, "fixture", "apiKey", "s3cret");
        await plugins.CheckSettingsAsync();
        state = plugins.States().Single();
        Assert.Equal(CliServerPluginStatuses.Loaded, state.Status);
        Assert.Null(state.Missing);
        Assert.Null(state.Error);
    }

    [Fact]
    public async Task APluginThatThrows_IsRolledBack_Failed_AndItsErrorKept_ThenClearedOnceItLoads()
    {
        var dir = TempDir();
        InstallFixture(dir);
        Directory.CreateDirectory(Path.Combine(dir, "plugin-data", "fixture"));
        File.WriteAllText(Path.Combine(dir, "plugin-data", "fixture", "fail"), "");
        var host = await StartAsync(dir);

        var state = host.Services.GetRequiredService<CliServerPlugins>().States().Single();
        Assert.Equal((CliServerPluginStatuses.Failed, "fixture asked to fail"), (state.Status, state.Error));
        Assert.Null(Processor(host, "fixture-hello"));
        // The service it added before throwing, and its settings, are gone
        Assert.DoesNotContain(host.Services.GetServices<ICliConfigProvider>(), p => p.Id == "fixture");
        Assert.DoesNotContain(host.Services.GetRequiredService<CliInstalledPlugins>().All, p => p.Id == FixturePackage);
        var plugins = new CliServerPlugins(new CliServerPluginsOptions { DataDir = dir });
        Assert.Equal("fixture asked to fail", plugins.Records().Single().Error);

        File.Delete(Path.Combine(dir, "plugin-data", "fixture", "fail"));
        var again = await StartAsync(dir);
        Assert.NotNull(Processor(again, "fixture-hello"));
        Assert.Null(plugins.Records().Single().Error);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("added")]
    [InlineData("removed")]
    [InlineData("no manifest")]
    public async Task FilesChangedSinceInstall_AreNotLoaded_Failed(string change)
    {
        var dir = TempDir();
        InstallFixture(dir);
        var plugins = new CliServerPlugins(new CliServerPluginsOptions { DataDir = dir });
        var versionDir = plugins.VersionDir(FixturePackage, "1.0.0");
        var record = plugins.Records().Single();
        Assert.Equal([$"{FixturePackage}.dll"], record.Files!.Keys);
        Assert.StartsWith("sha256-", record.Files![$"{FixturePackage}.dll"]);
        switch (change)
        {
            case "changed":
                File.AppendAllText(Path.Combine(versionDir, $"{FixturePackage}.dll"), "x");
                break;
            case "added":
                File.WriteAllBytes(Path.Combine(versionDir, "Evil.dll"), [1, 2, 3]);
                break;
            case "removed":
                plugins.Save([Record("fixture", FixturePackage, "1.0.0",
                    new Dictionary<string, string>(record.Files!) { ["Acme.Dep.dll"] = "sha256-AAAA" })]);
                break;
            default:
                plugins.Save([Record("fixture", FixturePackage, "1.0.0")]);
                break;
        }
        var host = await StartAsync(dir);
        var state = host.Services.GetRequiredService<CliServerPlugins>().States().Single();
        Assert.Equal((CliServerPluginStatuses.Failed, "files changed since install"), (state.Status, state.Error));
        Assert.Null(Processor(host, "fixture-hello"));
        Assert.Equal("files changed since install", new CliServerPlugins(new CliServerPluginsOptions { DataDir = dir }).Records().Single().Error);
    }

    [Fact]
    public async Task TheFilesystemApi_NeverServesTheDataDirectory_EvenBelowAnAllowedPath()
    {
        var root = TempDir();
        var dataDir = Path.Combine(root, "qcli-data");
        InstallFixture(dataDir);
        File.WriteAllText(Path.Combine(root, "notes.txt"), "hi");
        var host = await StartAsync(dataDir, cli => cli.AddFileSystem(o => o.AllowedPaths.Add(root)));
        var client = host.GetTestClient();
        Assert.Equal(200, (int)(await client.GetAsync($"/api/qcli/fs/cat?path={Uri.EscapeDataString(Path.Combine(root, "notes.txt"))}")).StatusCode);
        foreach (var path in new[] { dataDir, Path.Combine(dataDir, "server-plugins", "installed.json") })
        {
            var res = await client.GetAsync($"/api/qcli/fs/{(path == dataDir ? "ls" : "cat")}?path={Uri.EscapeDataString(path)}");
            Assert.Equal(403, (int)res.StatusCode);
        }
        var validator = host.Services.GetRequiredService<Qodalis.Cli.FileSystem.FileSystemPathValidator>();
        Assert.NotNull(validator.Validate(Path.Combine(root, "notes.txt")));
        Assert.Null(validator.Validate(Path.Combine(dataDir, "plugin-settings.json")));
    }

    [Fact]
    public async Task AtStartup_TheFoldersOfVersionsNoLongerListed_AreDeleted_BeforeAnythingLoads()
    {
        var dir = TempDir();
        InstallFixture(dir);
        var plugins = new CliServerPlugins(new CliServerPluginsOptions { DataDir = dir });
        void Folder(string relative)
        {
            var path = Path.Combine(plugins.Dir, relative);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "Old.dll"), "x");
        }
        Folder(Path.Combine(FixturePackage, "0.9.0"));                // updated while it was loaded
        Folder(Path.Combine(FixturePackage, "1.0.0.0a1b2c3d.tmp"));   // an interrupted install
        Folder(Path.Combine("Acme.Removed", "2.0.0"));                // removed while it was loaded
        Directory.CreateDirectory(Path.Combine(plugins.Dir, "Acme.Empty"));

        var host = await StartAsync(dir);
        Assert.NotNull(Processor(host, "fixture-hello"));
        Assert.Equal([FixturePackage], Directory.GetDirectories(plugins.Dir).Select(Path.GetFileName));
        Assert.Equal(["1.0.0"], Directory.GetDirectories(Path.Combine(plugins.Dir, FixturePackage)).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(plugins.Dir, CliServerPlugins.InstalledFile)));
    }

    [Fact]
    public void WithAnUnreadableList_NoFolderIsDeleted()
    {
        var dir = TempDir();
        InstallFixture(dir);
        var plugins = new CliServerPlugins(new CliServerPluginsOptions { DataDir = dir });
        Directory.CreateDirectory(Path.Combine(plugins.Dir, "Acme.Old", "1.0.0"));
        File.WriteAllText(Path.Combine(plugins.Dir, CliServerPlugins.InstalledFile), "{ not json");
        Assert.Empty(plugins.RemoveStaleVersions());
        Assert.True(Directory.Exists(Path.Combine(plugins.Dir, "Acme.Old", "1.0.0")));
        Assert.True(Directory.Exists(plugins.VersionDir(FixturePackage, "1.0.0")));
    }

    [Fact]
    public async Task MissingFiles_Fail_WithoutStoppingTheServer_OrTheOtherPlugins()
    {
        var dir = TempDir();
        InstallFixture(dir, more: [Record("gone", "Acme.Gone", "1.0.0")]);
        var host = await StartAsync(dir);
        var states = host.Services.GetRequiredService<CliServerPlugins>().States();
        var gone = states.Single(s => s.Name == "gone");
        Assert.Equal(CliServerPluginStatuses.Failed, gone.Status);
        Assert.StartsWith("not installed in", gone.Error);
        Assert.NotNull(Processor(host, "fixture-hello"));
        Assert.StartsWith("not installed in", new CliServerPlugins(new CliServerPluginsOptions { DataDir = dir })
            .Records().Single(r => r.Name == "gone").Error);
    }

    [Fact]
    public async Task APackageTheHostHas_IsSkipped()
    {
        var dir = TempDir();
        InstallFixture(dir, name: "weather", package: "Qodalis.Cli.Plugin.Weather", copy: false);
        var host = await StartAsync(dir);
        var state = host.Services.GetRequiredService<CliServerPlugins>().States().Single();
        Assert.Equal((CliServerPluginStatuses.Skipped, "already installed by the host"), (state.Status, state.Error));
    }

    [Fact]
    public async Task ABrokenInstalledList_LoadsNothing_AndDisabledPluginsLoadNone()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, CliServerPlugins.DirName));
        File.WriteAllText(Path.Combine(dir, CliServerPlugins.DirName, CliServerPlugins.InstalledFile), "{ not json");
        var host = await StartAsync(dir);
        Assert.Empty(host.Services.GetRequiredService<CliServerPlugins>().States());

        var other = TempDir();
        InstallFixture(other);
        var off = await StartAsync(other, cli => cli.ConfigureServerPlugins(o => o.Enabled = false));
        Assert.Empty(off.Services.GetRequiredService<CliServerPlugins>().States());
        Assert.Null(Processor(off, "fixture-hello"));
    }

    [Fact]
    public void PathsOutsideThePluginsDirectory_AreRefused()
    {
        var plugins = new CliServerPlugins(new CliServerPluginsOptions { DataDir = TempDir() });
        Assert.Throws<ArgumentException>(() => plugins.VersionDir("..", "1.0.0"));
        Assert.Throws<ArgumentException>(() => plugins.VersionDir("Acme", "../../x"));
        Assert.Throws<ArgumentException>(() => plugins.VersionDir("Acme/../..", "1.0.0"));
        Assert.EndsWith(Path.Combine("server-plugins", "Acme.Tools", "1.2.3"), plugins.VersionDir("Acme.Tools", "1.2.3"));
    }

    // ---------------------------------------------------------------- added in code

    [Fact]
    public async Task AddPlugin_RunsTheSameEntry_OptionsAreDefaultsWithSourceCode_SavedSettingsOverrideThem()
    {
        var dir = TempDir();
        var host = await StartAsync(dir, cli => cli.AddPlugin<CodeGreeterEntry>(new { greeting = "Hello", name = "Ana" }, id: "greeter"));

        var context = host.Services.GetRequiredService<CodeGreeterContext>();
        Assert.Equal(CliServerPluginSources.Code, context.Source);
        Assert.Equal("{\"greeting\":\"Hello\",\"name\":\"Ana\"}", context.Options);
        Assert.Equal("Hello Ana", await Processor(host, "code-greeter")!.HandleAsync(new CliProcessCommand { Command = "code-greeter" }));

        var config = host.Services.GetRequiredService<ICliConfigService>();
        var entries = (await config.GetAsync("greeter", new CliConfigContext(null, _ => true))).Sections.Single().Entries;
        Assert.Equal(("Hello", CliConfigSources.Code, CliConfigOrigins.Code), (entries[0].Value!.GetValue<string>(), entries[0].Source, entries[0].Origin));
        Assert.Equal(("Ana", CliConfigSources.Code, true), (entries[2].Value!.GetValue<string>(), entries[2].Source, entries[2].Required));
        Assert.Equal((CliConfigSources.Default, CliConfigOrigins.Default, true), (entries[1].Source, entries[1].Origin, entries[1].Required));
        Assert.Null(entries[1].Value);

        var plugins = await Checked(host);
        var state = plugins.States().Single();
        Assert.Equal((CliServerPluginStatuses.NeedsConfiguration, CliServerPluginSources.Code, "greeter"), (state.Status, state.Source, state.Name));
        Assert.Equal(["settings.token"], state.Missing);

        await SetAsync(host, "greeter", "greeting", "Salut");
        await SetAsync(host, "greeter", "token", "t");
        Assert.Equal("Salut Ana", await Processor(host, "code-greeter")!.HandleAsync(new CliProcessCommand { Command = "code-greeter" }));
        entries = (await config.GetAsync("greeter", new CliConfigContext(null, _ => true))).Sections.Single().Entries;
        Assert.Equal(CliConfigOrigins.Setting, entries[0].Origin);
        Assert.Equal((CliConfigOrigins.Setting, true, null), (entries[1].Origin, entries[1].IsSet, entries[1].Value));
        await plugins.CheckSettingsAsync();
        Assert.Equal(CliServerPluginStatuses.Loaded, plugins.States().Single().Status);
    }

    [Fact]
    public async Task PluginSettings_AreKeptInPluginSettingsJson_WhileTheServerKeepsSettingsInMemory_AndWinAfterARestart()
    {
        var dir = TempDir();
        var host = await StartAsync(dir, cli => cli.AddPlugin<CodeGreeterEntry>(new { name = "Ana" }, id: "greeter"));
        await SetAsync(host, "greeter", "name", "Ion");
        await SetAsync(host, "greeter", "token", "t0k3n");
        var file = Path.Combine(dir, CliServerPlugins.SettingsFile);
        Assert.True(File.Exists(file));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        var again = await StartAsync(dir, cli => cli.AddPlugin<CodeGreeterEntry>(new { name = "Ana" }, id: "greeter"));
        await again.Services.GetRequiredService<CliConfigRegistry>().ReadyAsync();
        Assert.Equal("Hi Ion", await Processor(again, "code-greeter")!.HandleAsync(new CliProcessCommand { Command = "code-greeter" }));
        var entries = (await again.Services.GetRequiredService<ICliConfigService>().GetAsync("greeter", new CliConfigContext(null, _ => true))).Sections.Single().Entries;
        Assert.Equal(("Ion", CliConfigOrigins.Setting), (entries[2].Value!.GetValue<string>(), entries[2].Origin));
        Assert.Equal(CliServerPluginStatuses.Loaded, (await Checked(again)).States().Single().Status);

        // A host with a store of its own keeps the plugins' settings there instead
        var other = TempDir();
        var stored = await StartAsync(other, cli =>
        {
            cli.SetConfigStore(new JsonFileConfigStore(Path.Combine(other, "host.json")));
            cli.AddPlugin<CodeGreeterEntry>(id: "greeter");
        });
        await SetAsync(stored, "greeter", "name", "Ana");
        Assert.False(File.Exists(Path.Combine(other, CliServerPlugins.SettingsFile)));
        Assert.Contains("greeter", File.ReadAllText(Path.Combine(other, "host.json")));
    }

    [Fact]
    public async Task TheInstalledList_AndInstalledJson_CarryStatusMissingAndSettings()
    {
        var dir = TempDir();
        InstallFixture(dir);
        var host = await StartAsync(dir, cli => cli.AddPlugin<CodeGreeterEntry>(id: "greeter"));
        await Checked(host);
        var all = host.Services.GetRequiredService<CliInstalledPlugins>().All;
        var fixture = all.Single(p => p.Id == FixturePackage);
        Assert.Equal((CliInstalledPlugin.MarketplaceSource, CliServerPluginStatuses.NeedsConfiguration, "fixture"), (fixture.Source, fixture.Status, fixture.Settings));
        Assert.Equal(["settings.apiKey"], fixture.Missing);
        var code = all.Single(p => p.Id == typeof(CodeGreeterEntry).Assembly.GetName().Name);
        Assert.Equal((CliInstalledPlugin.CodeSource, CliServerPluginStatuses.NeedsConfiguration, "greeter"), (code.Source, code.Status, code.Settings));
        Assert.Equal(["settings.token", "settings.name"], code.Missing);
        var json = System.Text.Json.JsonSerializer.Serialize(fixture);
        Assert.Contains("\"status\":\"needs-configuration\"", json);
        Assert.Contains("\"settings\":\"fixture\"", json);

        var record = new CliServerPlugins(new CliServerPluginsOptions { DataDir = dir }).Records().Single();
        Assert.Equal((CliServerPluginStatuses.NeedsConfiguration, "fixture", null), (record.Status, record.Settings, record.Error));
        Assert.Equal(["settings.apiKey"], record.Missing);

        await SetAsync(host, "fixture", "apiKey", "k");
        await host.Services.GetRequiredService<CliServerPlugins>().CheckSettingsAsync();
        Assert.Equal((CliServerPluginStatuses.Loaded, null), (fixture.Status, fixture.Missing));
        record = new CliServerPlugins(new CliServerPluginsOptions { DataDir = dir }).Records().Single();
        Assert.Equal((CliServerPluginStatuses.Loaded, null), (record.Status, record.Missing));
    }

    [Fact]
    public void TheLoadOrder_MatchesTheOtherServers()
    {
        Assert.Equal((10, 20, 30, 50, 70), (CliPluginOrder.Auth, CliPluginOrder.ConfigStore, CliPluginOrder.Core, CliPluginOrder.Default, CliPluginOrder.Source));
    }

    [Fact]
    public async Task AddPlugin_SectionOptions_AndAFailingCodePlugin_IsIsolated()
    {
        var host = await StartAsync(TempDir(), cli => cli.AddPlugin(new CodeGreeterEntry(), JsonNode.Parse("{\"settings\":{\"name\":\"Bo\"}}"), "greeter"));
        Assert.Equal("Hi Bo", await Processor(host, "code-greeter")!.HandleAsync(new CliProcessCommand { Command = "code-greeter" }));

        CodeGreeterEntry.Fail = true;
        var failed = await StartAsync(TempDir(), cli => cli.AddPlugin<CodeGreeterEntry>(id: "greeter"));
        var state = failed.Services.GetRequiredService<CliServerPlugins>().States().Single();
        Assert.Equal((CliServerPluginStatuses.Failed, "greeter asked to fail", CliServerPluginSources.Code), (state.Status, state.Error, state.Source));
        Assert.Null(Processor(failed, "code-greeter"));
        Assert.Null(failed.Services.GetService<CodeGreeterContext>());
        Assert.Throws<ArgumentException>(() => new CliBuilder(new ServiceCollection()).AddPlugin<CodeGreeterEntry>("not an object"));
    }

    [Fact]
    public async Task CodeAndInstalledPlugins_RunInTheirDeclaredOrder()
    {
        // The fixture declares Order = Source (700): it runs after a code plugin of the default order
        var dir = TempDir();
        InstallFixture(dir);
        var host = await StartAsync(dir, cli => cli.AddPlugin<CodeGreeterEntry>(id: "greeter"));
        var providers = host.Services.GetServices<ICliConfigProvider>().Select(p => p.Id).ToList();
        Assert.True(providers.IndexOf("greeter") < providers.IndexOf("fixture"));
        Assert.Equal(["greeter", "fixture"], host.Services.GetRequiredService<CliServerPlugins>().States().Select(s => s.Name));
    }
}
