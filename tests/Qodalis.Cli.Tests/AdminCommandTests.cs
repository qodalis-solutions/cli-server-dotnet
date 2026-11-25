using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Logs;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Commands;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Config;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>A module with one command (<c>forecast</c>) for the <c>admin plugins</c> tests.</summary>
internal sealed class WeatherTestModule : CliModule
{
    public override string Name => "weather";
    public override string Version => "2.0.0";
    public override string Description => "Weather";
    public override IEnumerable<ICliCommandProcessor> Processors { get; } = [new Forecast()];

    private sealed class Forecast : CliCommandProcessor
    {
        public override string Command { get; set; } = "forecast";
        public override string Description { get; set; } = "Forecast";

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult("sunny");
    }
}

/// <summary>
/// The admin plugin's <c>admin</c> terminal command through <c>POST /execute</c>: status (now, --history, --json),
/// plugins (list, enable, disable: idempotent, unknown, the admin module itself), clients, logs (filters, limits,
/// --since, bad arguments, the configured log store), and admin:access / logs:read on a protected server.
/// </summary>
public sealed class AdminCommandTests : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts) await host.DisposeAsync();
    }

    /// <summary>
    /// An open server with the admin plugin and the weather module. <paramref name="logs"/> is the log store (a buffer
    /// the host does not log into, so only the test's entries are there); <paramref name="time"/> the status sampler's clock.
    /// </summary>
    private async Task<ConfigTestHost> StartAsync(ILogStore? logs = null, ManualTimeProvider? time = null, SystemMemoryReaders? memory = null)
    {
        var host = await ConfigTestHost.StartAsync(cli =>
        {
            cli.AddModule(new WeatherTestModule());
            if (memory != null) cli.Services.AddSingleton(memory);
            cli.Services.AddSingleton<ILogStore>(logs ?? new LogRingBuffer());
            if (time != null) cli.Services.AddSingleton(sp => new StatusHistory(sp, StatusHistory.DefaultCapacity, time));
        }, admin: true);
        _hosts.Add(host);
        return host;
    }

    /// <summary>A server whose startup sample lands two days ago, out of every --history window.</summary>
    private async Task<(ConfigTestHost Host, StatusHistory History)> StartWithoutRecentSampleAsync()
    {
        var time = new ManualTimeProvider();
        var now = DateTimeOffset.UtcNow;
        time.SetUtcNow(now.AddDays(-2));
        var host = await StartAsync(time: time);
        var history = await FirstSampleAsync(host);
        time.SetUtcNow(now);
        return (host, history);
    }

    /// <summary>Waits for the sampler's first sample, taken in the background once the host started.</summary>
    private static async Task<StatusHistory> FirstSampleAsync(ConfigTestHost host)
    {
        var history = host.Service<StatusHistory>();
        for (var i = 0; i < 500 && history.Latest == null; i++) await Task.Delay(10);
        Assert.NotNull(history.Latest);
        return history;
    }

    private static async Task<(int Status, string Text, JsonElement Body)> Run(ConfigTestHost h, string line, string? token = null) =>
        await ConfigCommands.RunAsync(h.Client, h.Service<ICliCommandRegistry>(), line, token);

    private static async Task<string> Text(ConfigTestHost h, string line) => (await Run(h, line)).Text;

    private static string[] Lines(string text) => text.Split('\n');

    // ---- registration ----

    [Fact]
    public async Task IsTheAdminModule_WithStatusPluginsClientsLogsRestartAndDashboard_AdminAccessOnTheRoot_LogsReadOnLogs()
    {
        var h = await StartAsync();
        var root = h.Service<ICliCommandRegistry>().FindProcessor("admin")!;
        Assert.Equal(["admin:access"], root.RequiredPermissions!);
        Assert.Equal(["status -", "plugins -", "clients -", "logs logs:read", "restart -", "dashboard -"],
            root.Processors!.Select(p => $"{p.Command} {(p.RequiredPermissions == null ? "-" : string.Join(",", p.RequiredPermissions))}"));
        Assert.Contains(h.Service<ModuleRegistry>().List(), m => m.Id == "admin" && m.Processors.SequenceEqual(["admin"]));
        var plugins = root.Processors!.Single(p => p.Command == "plugins");
        Assert.Equal(["list", "enable", "disable"], plugins.Processors!.Select(p => p.Command));
    }

    // ---- admin status ----

    [Fact]
    public async Task Status_PrintsVersionPlatformUptimeMemoryCpuConnectionsCommandsJobsAndFeatures()
    {
        var h = await StartAsync();
        var (_, text, body) = await Run(h, "admin status");
        Assert.Equal(0, body.GetProperty("exitCode").GetInt32());
        var lines = Lines(text);
        Assert.Matches(@"^Server {6}Qodalis\.Cli \S+", lines[0]);
        Assert.Equal($"Platform    dotnet {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.OSDescription})", lines[1]);
        Assert.Matches(@"^Uptime {6}\d+ (s|min|h \d+ min|d \d+ h) \(since \d{4}-\d\d-\d\d \d\d:\d\d:\d\d\)$", lines[2]);
        Assert.Matches(@"^Memory {6}[\d.]+ MB( \(process\) · (machine|container) .+)?$", lines[3]);
        Assert.Matches(@"^CPU {9}(n/a|[\d.]+%)$", lines[4]);
        Assert.Equal("Connections 0 WebSocket, 0 shell", lines[5]);
        Assert.Matches(@"^Commands {4}\d+$", lines[6]);
        Assert.Equal("Jobs        0", lines[7]);
        Assert.Equal("Features    none", lines[8]);
        // admin alone is the status
        Assert.Equal(lines[0], Lines(await Text(h, "admin"))[0]);
    }

    [Fact]
    public async Task StatusJson_TheGetStatusPayload_WithTheServerPackage()
    {
        var h = await StartAsync();
        var body = JsonDocument.Parse(await Text(h, "admin status --json")).RootElement;
        Assert.Equal("dotnet", body.GetProperty("platform").GetString());
        Assert.Equal("Qodalis.Cli", body.GetProperty("server").GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Number, body.GetProperty("memoryUsageMb").ValueKind);
        Assert.True(body.TryGetProperty("systemMemoryTotalMb", out _));
        Assert.True(body.TryGetProperty("systemMemoryUsedMb", out _));
        Assert.True(body.TryGetProperty("memoryLimited", out _));
        Assert.True(body.TryGetProperty("cpuUsagePercent", out _));
        Assert.Equal(RuntimeInformation.OSDescription, body.GetProperty("os").GetString());
    }

    private const long Mb = 1024 * 1024;
    private const long Gb = 1024 * Mb;

    private static SystemMemoryReaders Memory(
        Func<long?>? installed = null, Func<long?>? available = null, Func<long?>? limit = null, Func<long?>? usage = null) => new()
        {
            InstalledBytes = installed ?? (() => 16 * Gb),
            AvailableBytes = available ?? (() => 6 * Gb),
            LimitBytes = limit ?? (() => null),
            ContainerUsageBytes = usage ?? (() => null),
        };

    private static long? Throws() => throw new IOException("unreadable");

    [Fact]
    public async Task StatusMemory_TheMachineUse()
    {
        var h = await StartAsync(memory: Memory());
        Assert.Matches(@"(?m)^Memory {6}[\d.]+ MB \(process\) · machine 10 GB of 16 GB used \(63%\)$", await Text(h, "admin status"));
        var body = JsonDocument.Parse(await Text(h, "admin status --json")).RootElement;
        Assert.Equal(16384, body.GetProperty("systemMemoryTotalMb").GetInt64());
        Assert.Equal(10240, body.GetProperty("systemMemoryUsedMb").GetInt64());
        Assert.False(body.GetProperty("memoryLimited").GetBoolean());
    }

    [Fact]
    public async Task StatusMemory_TheContainerLimitAndItsUsage()
    {
        var h = await StartAsync(memory: Memory(limit: () => 512 * Mb, usage: () => 300 * Mb));
        Assert.Matches(@"(?m)^Memory {6}[\d.]+ MB \(process\) · container 300 MB of 512 MB used \(59%\)$", await Text(h, "admin status"));
        var body = JsonDocument.Parse(await Text(h, "admin status --json")).RootElement;
        Assert.Equal(512, body.GetProperty("systemMemoryTotalMb").GetInt64());
        Assert.Equal(300, body.GetProperty("systemMemoryUsedMb").GetInt64());
        Assert.True(body.GetProperty("memoryLimited").GetBoolean());
        // The samples carry it too
        var sample = (await FirstSampleAsync(h)).Latest!;
        Assert.Equal((300L, 512L), (sample.SystemMemoryUsedMb, sample.SystemMemoryTotalMb));
    }

    [Fact]
    public async Task StatusMemory_Unreadable_IsNull_AndTheLineIsTheProcessAlone()
    {
        var h = await StartAsync(memory: Memory(installed: Throws, available: Throws, limit: Throws, usage: Throws));
        Assert.Matches(@"(?m)^Memory {6}[\d.]+ MB$", await Text(h, "admin status"));
        var body = JsonDocument.Parse(await Text(h, "admin status --json")).RootElement;
        foreach (var key in new[] { "systemMemoryTotalMb", "systemMemoryUsedMb", "memoryLimited" })
            Assert.Equal(JsonValueKind.Null, body.GetProperty(key).ValueKind);
    }

    [Fact]
    public void SystemMemory_ReadsLimitsAndNulls()
    {
        Assert.Equal(new SystemMemoryInfo(16384, 10240, false), SystemMemory.Read(Memory()));
        Assert.Equal(new SystemMemoryInfo(512, 300, true), SystemMemory.Read(Memory(limit: () => 512 * Mb, usage: () => 300 * Mb)));
        // A limit at or above the installed RAM (cgroup v1's "no limit" huge number) is no limit
        Assert.Equal(new SystemMemoryInfo(16384, 10240, false), SystemMemory.Read(Memory(limit: () => 16 * Gb)));
        Assert.Equal(new SystemMemoryInfo(16384, 10240, false), SystemMemory.Read(Memory(limit: () => long.MaxValue)));
        // Limited without a readable usage: not the machine's
        Assert.Equal(new SystemMemoryInfo(512, null, true), SystemMemory.Read(Memory(limit: () => 512 * Mb)));
        Assert.Equal(new SystemMemoryInfo(16384, null, false), SystemMemory.Read(Memory(available: Throws)));
        Assert.Equal(SystemMemoryInfo.Unknown, SystemMemory.Read(Memory(installed: Throws, limit: Throws)));
        // This machine
        var here = SystemMemory.Read();
        Assert.True(here.TotalMb > 0);
        Assert.NotNull(here.Limited);
    }

    [Fact]
    public void MemoryLine_TheProcessThenTheMachineOrContainer()
    {
        static string Line(long? total, long? used, bool? limited) => AdminFormat.MemoryLine(new StatusSnapshot
        {
            MemoryUsageMb = 146, SystemMemoryTotalMb = total, SystemMemoryUsedMb = used, MemoryLimited = limited,
            StartedAt = "", Platform = "", PlatformVersion = "", Os = "", EnabledFeatures = [],
        });
        Assert.Equal("146 MB (process) · machine 9.8 GB of 16 GB used (61%)", Line(16384, 10035, false));
        Assert.Equal("146 MB (process) · container 300 MB of 512 MB used (59%)", Line(512, 300, true));
        Assert.Equal("146 MB (process) · machine 16 GB", Line(16384, null, false));
        Assert.Equal("146 MB (process) · container limit 512 MB", Line(512, null, true));
        Assert.Equal("146 MB", Line(null, null, null));
        Assert.Equal("1023 MB", AdminFormat.MemorySize(1023));
        Assert.Equal("1 GB", AdminFormat.MemorySize(1024));
        Assert.Equal("1.5 GB", AdminFormat.MemorySize(1536));
    }

    [Fact]
    public async Task Status_ShowsTheLatestCpuSample_WhenThereIsOne()
    {
        var h = await StartAsync();
        (await FirstSampleAsync(h)).Add(new StatusSample(DateTime.UtcNow, 10, 3.5, 0, 0));
        Assert.Contains("CPU         3.5%", await Text(h, "admin status"));
    }

    [Fact]
    public async Task StatusHistory_CountTimeSpanMinAvgMaxMemoryAndCpu_PeakConnections()
    {
        var (h, history) = await StartWithoutRecentSampleAsync();
        var now = DateTime.UtcNow;
        DateTime At(int minutesAgo) => now.AddMinutes(-minutesAgo);
        history.Add(new StatusSample(At(120), 999, 99, 9, 9));
        history.Add(new StatusSample(At(30), 100, null, 1, 0));
        history.Add(new StatusSample(At(20), 110, 2, 3, 1));
        history.Add(new StatusSample(At(10), 120, 4, 2, 0));

        var lines = Lines(await Text(h, "admin status --history"));
        Assert.Matches(@"^Last 60 min: 3 sample\(s\) every 15 s, \S+ \S+ to \S+ \S+$", lines[0]);
        Assert.Equal("Memory      min 100 MB  avg 110 MB  max 120 MB", lines[1]);
        Assert.Equal("CPU         min 2%  avg 3%  max 4%", lines[2]);
        Assert.Equal("Peak        3 WebSocket, 1 shell", lines[3]);
        Assert.Contains("4 sample(s)", await Text(h, "admin status --history --minutes 180"));

        var json = JsonDocument.Parse(await Text(h, "admin status --history --minutes 15 --json")).RootElement;
        Assert.Equal(15, json.GetProperty("minutes").GetInt32());
        Assert.Equal(15, json.GetProperty("intervalSeconds").GetInt32());
        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.Equal("""{"min":120,"avg":120,"max":120}""", JsonSerializer.Serialize(json.GetProperty("memoryMb")));
    }

    [Fact]
    public async Task StatusHistory_WithoutSamples_AndCpuNa_WhenNoSampleHasIt()
    {
        var (h, history) = await StartWithoutRecentSampleAsync();
        Assert.Equal("No status samples in the last 60 min.", await Text(h, "admin status --history"));
        history.Add(new StatusSample(DateTime.UtcNow, 50, null, 0, 0));
        Assert.Contains("CPU         n/a", await Text(h, "admin status --history"));
    }

    [Fact]
    public async Task Status_RefusesBadMinutes_AndMinutesWithoutHistory()
    {
        var h = await StartAsync();
        foreach (var bad in new[] { "0", "1441", "x", "1.5" })
            Assert.Equal("Error: --minutes must be a whole number from 1 to 1440", await Text(h, $"admin status --history --minutes {bad}"));
        Assert.Equal("Error: --minutes goes with --history", await Text(h, "admin status --minutes 5"));
    }

    // ---- admin plugins ----

    [Fact]
    public async Task Plugins_ListsTheModulesInColumns_AndAsJson()
    {
        var h = await StartAsync();
        var text = await Text(h, "admin plugins");
        var lines = Lines(text);
        Assert.Matches(@"^ID\s+Name\s+Version\s+Status\s+Commands$", lines[0]);
        Assert.Matches(@"^weather\s+weather\s+2\.0\.0\s+enabled\s+1$", lines.Single(l => l.StartsWith("weather")));
        Assert.Matches(@"^admin\s+admin\s+\S+\s+enabled\s+1$", lines.Single(l => l.StartsWith("admin")));
        Assert.Equal(text, await Text(h, "admin plugins list"));
        Assert.Equal(text, await Text(h, "admin modules ls"));
        var json = JsonDocument.Parse(await Text(h, "admin plugins --json")).RootElement;
        var weather = json.EnumerateArray().Single(m => m.GetProperty("id").GetString() == "weather");
        Assert.True(weather.GetProperty("enabled").GetBoolean());
        Assert.Equal("""["forecast"]""", JsonSerializer.Serialize(weather.GetProperty("processors")));
    }

    [Fact]
    public async Task Plugins_DisableBlocksTheCommands_EnableBringsThemBack_BothIdempotent_IdsCaseInsensitive()
    {
        var h = await StartAsync();
        Assert.Equal("sunny", await Text(h, "forecast"));
        Assert.Equal("Disabled weather (1 command).", await Text(h, "admin plugins disable Weather"));
        Assert.Equal("weather is already disabled.", await Text(h, "admin plugins disable weather"));
        Assert.Matches(@"weather\s+weather\s+2\.0\.0\s+disabled", await Text(h, "admin plugins"));
        var blocked = await Run(h, "forecast");
        Assert.Equal(1, blocked.Body.GetProperty("exitCode").GetInt32());
        Assert.Contains("disabled", blocked.Text);
        Assert.Equal("Enabled weather (1 command).", await Text(h, "admin plugins enable weather"));
        Assert.Equal("weather is already enabled.", await Text(h, "admin plugins enable weather"));
        Assert.Equal("sunny", await Text(h, "forecast"));
    }

    [Fact]
    public async Task Plugins_TheSameStateAsTheDashboardToggle()
    {
        var h = await StartAsync();
        await Text(h, "admin plugins disable weather");
        await h.SignInAdminAsync();
        var plugin = JsonDocument.Parse(await h.Client.GetStringAsync("/api/v1/qcli/plugins/weather")).RootElement;
        Assert.False(plugin.GetProperty("enabled").GetBoolean());
        // the dashboard's toggle turns it back on, and the terminal sees it
        (await h.Client.PostAsync("/api/v1/qcli/plugins/weather/toggle", null)).EnsureSuccessStatusCode();
        Assert.Equal("weather is already enabled.", await Text(h, "admin plugins enable weather"));
    }

    [Fact]
    public async Task Plugins_RefusesAnUnknownModule_AMissingOrExtraId_AndDisablingTheAdminModule()
    {
        var h = await StartAsync();
        Assert.Equal("Error: unknown module: nope (admin plugins lists them)", await Text(h, "admin plugins enable nope"));
        Assert.Equal("Error: name one module: admin plugins disable <id> (admin plugins lists them)", await Text(h, "admin plugins disable"));
        Assert.Equal("Error: name one module: admin plugins enable <id> (admin plugins lists them)", await Text(h, "admin plugins enable a b"));
        Assert.Equal("Error: the admin module cannot be disabled from the terminal: it would disable this command",
            await Text(h, "admin plugins disable admin"));
        Assert.Equal("Error: the admin module cannot be disabled from the terminal: it would disable this command",
            await Text(h, "admin plugins disable ADMIN"));
        // enabling it is harmless
        Assert.Equal("admin is already enabled.", await Text(h, "admin plugins enable admin"));
    }

    // ---- admin clients ----

    [Fact]
    public async Task Clients_SaysSo_WhenNobodyIsConnected()
    {
        var h = await StartAsync();
        Assert.Equal("No WebSocket clients connected.", await Text(h, "admin clients"));
        Assert.Equal("[]", await Text(h, "admin clients --json"));
    }

    [Fact]
    public async Task Clients_ListsTheConnectedClientsInColumns()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.ProtectServer = false, configureCli: cli => cli.AddAdmin());
        h.Events.Clients.Add(new CliWebSocketClientInfo { Id = "c1", ConnectedAt = "2026-11-25T10:00:00.000Z", RemoteAddress = "127.0.0.1", Type = "events" });
        h.Events.Clients.Add(new CliWebSocketClientInfo { Id = "client-2", ConnectedAt = "2026-11-25T11:30:15.5000000Z", RemoteAddress = "", Type = "events" });
        var registry = h.Service<ICliCommandRegistry>();
        Assert.Equal(string.Join('\n',
                "2 client(s)",
                "ID        Type    Connected            Address",
                "c1        events  2026-11-25 10:00:00  127.0.0.1",
                "client-2  events  2026-11-25 11:30:15  -"),
            (await ConfigCommands.RunAsync(h.Client, registry, "admin clients")).Text);
        Assert.Equal(2, JsonDocument.Parse((await ConfigCommands.RunAsync(h.Client, registry, "admin clients --json")).Text).RootElement.GetArrayLength());
    }

    // ---- admin logs ----

    private async Task<ConfigTestHost> WithLogsAsync()
    {
        var buffer = new LogRingBuffer();
        var start = DateTime.UtcNow.AddHours(-3);
        foreach (var (minutes, level, source, message) in new[]
                 {
                     (0, "INFO", "server", "started"),
                     (60, "DEBUG", "jobs", "tick"),
                     (120, "WARN", "jobs", "slow job cleanup"),
                     (170, "ERROR", "db", "connection timeout"),
                     (175, "INFO", "server", "request served"),
                 })
            buffer.Add(new LogEntry { Timestamp = start.AddMinutes(minutes), Level = level, Source = source, Message = message });
        return await StartAsync(buffer);
    }

    [Fact]
    public async Task Logs_PrintsTheLatestEntriesOldestFirst_TimeLevelSourceMessage()
    {
        var h = await WithLogsAsync();
        var lines = Lines(await Text(h, "admin logs"));
        Assert.Equal(5, lines.Length);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d INFO  \[server\] started$", lines[0]);
        Assert.Matches(@"ERROR \[db\] connection timeout$", lines[3]);
    }

    [Fact]
    public async Task Logs_LimitKeepsTheNewest_AndSaysHowManyThereWere()
    {
        var h = await WithLogsAsync();
        var lines = Lines(await Text(h, "admin logs -n 2"));
        Assert.Equal("Last 2 of 5 entries", lines[0]);
        Assert.Contains("connection timeout", lines[1]);
        Assert.Contains("request served", lines[2]);
        Assert.Equal(5, Lines(await Text(h, "admin logs --limit 5")).Length);
    }

    [Fact]
    public async Task Logs_FiltersByLevel_Search_AndSince()
    {
        var h = await WithLogsAsync();
        Assert.Equal(2, Lines(await Text(h, "admin logs --level warn,error")).Length);
        Assert.Contains("slow job cleanup", await Text(h, "admin logs --level WARNING"));
        Assert.Contains("connection timeout", await Text(h, "admin logs --search TIMEOUT"));
        Assert.Equal(2, Lines(await Text(h, "admin logs --search jobs")).Length);
        Assert.Equal(2, Lines(await Text(h, "admin logs --since 30m")).Length);
        var iso = DateTime.UtcNow.AddMinutes(-65).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        Assert.Equal(3, Lines(await Text(h, $"admin logs --since {iso}")).Length);
        Assert.Equal("No log entries match.", await Text(h, "admin logs --search nothing-like-this"));
        // entries of this process carry no service (a persistent store such as Seq has them)
        Assert.Equal("No log entries match.", await Text(h, "admin logs --service jobs"));
    }

    [Fact]
    public async Task LogsJson_TheEntriesAndTheTotal()
    {
        var h = await WithLogsAsync();
        var json = JsonDocument.Parse(await Text(h, "admin logs -n 1 --json")).RootElement;
        Assert.Equal(5, json.GetProperty("total").GetInt32());
        var entry = Assert.Single(json.GetProperty("entries").EnumerateArray());
        Assert.Equal("INFO", entry.GetProperty("level").GetString());
        Assert.Equal("request served", entry.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Logs_SaysSo_WhenTheStoreIsEmpty()
    {
        var h = await StartAsync();
        Assert.Equal("No log entries match.", await Text(h, "admin logs"));
    }

    [Fact]
    public async Task Logs_RefusesBadArguments()
    {
        var h = await WithLogsAsync();
        Assert.Equal("Error: unknown level fatal (debug, info, warn, error)", await Text(h, "admin logs --level fatal,info"));
        Assert.Equal("Error: --limit must be a whole number from 1 to 1000", await Text(h, "admin logs -n 0"));
        Assert.Equal("Error: --limit must be a whole number from 1 to 1000", await Text(h, "admin logs --limit 1001"));
        Assert.Equal("Error: --since must be a duration (30s, 5m, 1h, 7d) or an ISO 8601 date", await Text(h, "admin logs --since yesterday"));
        Assert.Equal("Error: --since must be more than 0", await Text(h, "admin logs --since 0m"));
        Assert.Equal("Error: --since is in the future", await Text(h, "admin logs --since 2999-01-01"));
        Assert.Equal("Error: --search needs a value", await Text(h, "admin logs --search"));
        Assert.Equal("Error: --level needs a value", await Text(h, "admin logs --level"));
    }

    [Fact]
    public async Task Logs_ReadTheConfiguredLogStore_PagingPastItsLimit_WithTheFilters()
    {
        var store = new FakeLogStore(1200);
        var h = await StartAsync(store);
        var lines = Lines(await Text(h, "admin logs -n 1000 --level info --search x --service api --since 1h"));
        Assert.Equal("Last 1000 of 1200 entries", lines[0]);
        Assert.EndsWith("message 0", lines[^1]); // the newest last
        Assert.EndsWith("message 999", lines[1]);
        Assert.Equal([(500, 0), (500, 500)], store.Pages);
        var options = store.Last!;
        Assert.Equal(["INFO"], options.Levels);
        Assert.Equal("x", options.Search);
        Assert.Equal("api", options.Service);
        Assert.InRange(options.From!.Value, DateTimeOffset.UtcNow.AddMinutes(-61), DateTimeOffset.UtcNow.AddMinutes(-59));

        store.Fail = true;
        Assert.Equal("Error: the log store (fake) is unavailable: down", await Text(h, "admin logs"));
    }

    // ---- protected server ----

    [Fact]
    public async Task OnAProtectedServer_NeedsAdminAccess_AndLogsReadForAdminLogs()
    {
        var buffer = new LogRingBuffer();
        buffer.Add(new LogEntry { Level = "INFO", Source = "x", Message = "hello" });
        await using var h = await UsersTestHost.StartAsync(o => o.MinPasswordLength = 4, configureCli: cli =>
        {
            cli.AddModule(new WeatherTestModule());
            cli.Services.AddSingleton<ILogStore>(buffer);
            cli.AddAdmin();
        });
        var registry = h.Service<ICliCommandRegistry>();
        var root = await h.RootTokenAsync();
        var runner = await h.UserWithPermissionsAsync(root, "runner", "commands:execute");
        var admin = await h.UserWithPermissionsAsync(root, "boss", "commands:execute", "admin:access");
        var logger = await h.UserWithPermissionsAsync(root, "logger", "commands:execute", "admin:access", "logs:read");
        Task<(int Status, string Text, JsonElement Body)> Run(string line, string? token = null) =>
            ConfigCommands.RunAsync(h.Client, registry, line, token);

        Assert.Equal(401, (await Run("admin status")).Status);
        var denied = await Run("admin status", runner);
        Assert.Equal(403, denied.Status);
        Assert.Contains("admin:access", denied.Body.GetProperty("error").GetString());
        Assert.Equal(403, (await Run("admin plugins disable weather", runner)).Status);

        Assert.Contains("Platform", (await Run("admin status", admin)).Text);
        Assert.Contains("weather", (await Run("admin plugins", admin)).Text);
        var noLogs = await Run("admin logs", admin);
        Assert.Equal(403, noLogs.Status);
        Assert.Contains("logs:read", noLogs.Body.GetProperty("error").GetString());
        Assert.Contains("hello", (await Run("admin logs", logger)).Text);
        Assert.Equal("No WebSocket clients connected.", (await Run("admin clients", root)).Text);
    }

    // ---- helpers ----

    [Fact]
    public void FormatDuration()
    {
        Assert.Equal("0 s", AdminFormat.Duration(0));
        Assert.Equal("59 s", AdminFormat.Duration(59));
        Assert.Equal("1 min", AdminFormat.Duration(60));
        Assert.Equal("2 h 3 min", AdminFormat.Duration(7384));
        Assert.Equal("1 d 4 h", AdminFormat.Duration(90_000 + 3 * 3600));
    }

    [Fact]
    public void FormatTable_PadsEveryColumnButTheLast()
    {
        Assert.Equal("A   Bee\nxx  y\nz", AdminFormat.Table(["A", "Bee"], [["xx", "y"], ["z", ""]]));
    }

    [Fact]
    public void FormatLogLine_WithoutASource()
    {
        Assert.Equal("2026-11-25 10:00:00 WARN  m",
            AdminFormat.LogLine(new LogRecord { Timestamp = DateTimeOffset.Parse("2026-11-25T10:00:00.000Z"), Level = "WARN", Source = "", Message = "m" }));
    }

    [Fact]
    public void SummarizeStatusHistory_OfNothing()
    {
        var summary = StatusHistorySummary.Of([]);
        Assert.Equal(0, summary.Count);
        Assert.Null(summary.From);
        Assert.Null(summary.MemoryMb);
        Assert.Null(summary.CpuPercent);
        Assert.Equal(0, summary.MaxWsConnections);
        Assert.Equal(0, summary.MaxShellSessions);
    }

    [Fact]
    public void SummarizeStatusHistory_KeepsTwoDecimalsInTheAverage()
    {
        StatusSample Sample(double memory) => new(new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc), memory, null, 0, 0);
        Assert.Equal(new StatusRange(213.95, 213.97, 214), StatusHistorySummary.Of([Sample(213.95), Sample(213.95), Sample(214)]).MemoryMb);
    }

    [Fact]
    public void ParseSince()
    {
        var now = DateTimeOffset.Parse("2026-11-25T12:00:00Z");
        string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        Assert.Equal("2026-11-25T11:59:30.000Z", Iso(AdminQueryTime.ParseSince("30s", "s", now)));
        Assert.Equal("2026-11-25T10:00:00.000Z", Iso(AdminQueryTime.ParseSince("2H", "s", now)));
        Assert.Equal("2026-11-18T12:00:00.000Z", Iso(AdminQueryTime.ParseSince("7d", "s", now)));
        Assert.Equal("2026-11-25T08:00:00.000Z", Iso(AdminQueryTime.ParseSince("2026-11-25T08:00", "s", now)));
        Assert.Equal("2026-11-25T07:00:00.000Z", Iso(AdminQueryTime.ParseSince("2026-11-25T09:00+02:00", "s", now)));
        Assert.Throws<AdminQueryException>(() => AdminQueryTime.ParseSince("5w", "s", now));
    }

    /// <summary>A store of <c>count</c> entries (newest first, <c>message 0</c> the newest) that records the queries.</summary>
    private sealed class FakeLogStore(int count) : ILogStore
    {
        public List<(int Limit, int Offset)> Pages { get; } = [];
        public LogQueryOptions? Last { get; private set; }
        public bool Fail { get; set; }

        public string Name => "fake";
        public bool Persistent => true;

        public Task<LogPage> QueryAsync(LogQueryOptions options, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new HttpRequestException("down");
            Pages.Add((options.Limit, options.Offset));
            Last = options;
            var now = DateTimeOffset.UtcNow;
            var entries = Enumerable.Range(options.Offset, Math.Max(0, Math.Min(options.Limit, count - options.Offset)))
                .Select(i => new LogRecord { Timestamp = now.AddSeconds(-i), Level = "INFO", Source = "s", Message = $"message {i}" })
                .ToList();
            return Task.FromResult(new LogPage { Entries = entries, Total = count, HasMore = options.Offset + entries.Count < count });
        }

        public Task<IReadOnlyList<string>> GetServicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
