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
using Qodalis.Cli.Plugin.Docs;
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
        Assert.Equal(["filesystem-os.settings.roots"], state.Missing);
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
        Assert.Equal(["filesystem-s3.settings.bucket"], state.Missing);
        await Assert.ThrowsAsync<FileStorageNotConfiguredError>(() => s3.Services.GetRequiredService<IFileStorageProvider>().ListAsync("/"));
        var view = await s3.Services.GetRequiredService<ICliConfigService>().GetAsync("filesystem-s3", new CliConfigContext(null, _ => true));
        Assert.True(view.Sections.Single().Entries.Single(e => e.Key == "bucket").Required);
    }
}
