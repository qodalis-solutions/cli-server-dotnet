using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Abstractions.Notifications;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>The periodic check for updates (<c>AddAdmin(o =&gt; o.UpdateChecks...)</c>); "Check now" works either way.</summary>
public sealed class AdminUpdateCheckOptions
{
    /// <summary>Check periodically (default true).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Between checks (default 6 hours).</summary>
    public TimeSpan Interval { get; set; } = UpdateChecker.DefaultInterval;

    /// <summary>The first check after a start (default 60 s; a fresher last result waits until it is due).</summary>
    public TimeSpan InitialDelay { get; set; } = UpdateChecker.DefaultInitialDelay;

    /// <summary>Throws <see cref="ArgumentException"/> for an interval that is not positive or a negative delay.</summary>
    public void Validate()
    {
        if (Interval <= TimeSpan.Zero) throw new ArgumentException("UpdateChecks.Interval must be positive", nameof(Interval));
        if (InitialDelay < TimeSpan.Zero) throw new ArgumentException("UpdateChecks.InitialDelay must not be negative", nameof(InitialDelay));
    }
}

/// <summary>
/// What the update checker reads: the marketplaces' annotated lists (as their <c>SearchAsync</c> answers), what is
/// installed from them, the running server version and the NuGet feed. Tests pass their own.
/// </summary>
public sealed class UpdateCheckerSources
{
    /// <summary>A page of the dashboard marketplace's annotated list (<c>DashboardMarketplace.SearchAsync</c>); null when it is off.</summary>
    public Func<int, int, CancellationToken, Task<JsonObject>>? DashboardSearch { get; init; }

    /// <summary>The dashboard plugins installed from the marketplace (name, version).</summary>
    public Func<IEnumerable<(string Name, string Version)>>? DashboardInstalled { get; init; }

    /// <summary>A page of the server marketplace's annotated list for this platform (<c>ServerPluginMarketplace.SearchAsync</c>); null when it is off.</summary>
    public Func<int, int, CancellationToken, Task<JsonObject>>? ServerSearch { get; init; }

    /// <summary>The server plugins installed from the marketplace (name, version).</summary>
    public Func<IEnumerable<(string Name, string Version)>>? ServerInstalled { get; init; }

    /// <summary>The running server's version (the <c>Qodalis.Cli</c> package).</summary>
    public required Func<string?> ServerVersion { get; init; }

    /// <summary>The NuGet flat container the server package's newest version is read from.</summary>
    public string NuGetFeed { get; init; } = ServerPluginMarketplace.DefaultNuGetFeed;

    /// <summary>The HTTP handler of the NuGet request (the marketplace options'); null for a real one.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Where the updates are published as notifications.</summary>
    public Func<ICliNotifications?>? Notifications { get; init; }

    /// <summary>The jobs plugin's registry when it is mounted (read when the checks start).</summary>
    public Func<ICliJobRegistry?>? Jobs { get; init; }

    /// <summary>The file the last result is kept in (none: memory only).</summary>
    public string? File { get; init; }

    public TimeProvider? Time { get; init; }

    public ILogger? Logger { get; init; }
}

/// <summary>
/// Checks the marketplace (and NuGet for the server package) for newer versions: dashboard and server plugins installed
/// from the marketplace (updated here), plugins the host adds in code (updated in code), and the server package. Every
/// update becomes a notification (<c>update:…</c>, permission <c>updates:read</c>) resolved once it no longer applies.
/// Runs every 6 hours (a job of the jobs plugin when one is mounted, else a timer), the first time 60 s after a start,
/// and on demand (<c>POST /api/v1/qcli/updates/check</c>); the last result is kept in <c>&lt;data dir&gt;/updates.json</c>.
/// The same contract as the Node and Python servers.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    /// <summary>How often the server checks for updates.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);

    /// <summary>The first check after a start (unless the last result is fresher than the interval).</summary>
    public static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(60);

    /// <summary>The job id when the jobs plugin schedules the check.</summary>
    public const string JobId = "updates:check";

    /// <summary>The file the last result is kept in, below the data directory.</summary>
    public const string FileName = "updates.json";

    /// <summary>The dashboard tab the update notifications open.</summary>
    public const string Link = "/plugins?tab=updates";

    /// <summary>The API's route.</summary>
    public const string ApiPath = "/api/v1/qcli/updates";

    /// <summary>The server package (every <c>Qodalis.Cli*</c> package shares its version).</summary>
    public const string ServerPackage = ServerPluginMarketplace.ServerCorePackage;

    private const int MaxPages = 10;
    private const int PageSize = 100;

    private readonly UpdateCheckerSources _sources;
    private readonly AdminUpdateCheckOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _lock = new();
    private readonly List<ITimer> _timers = [];
    private CliUpdatesResult _last;
    private Task<CliUpdatesResult>? _running;
    private bool _started;

    public UpdateChecker(UpdateCheckerSources sources, AdminUpdateCheckOptions? options = null)
    {
        _sources = sources;
        _options = options ?? new AdminUpdateCheckOptions();
        _options.Validate();
        _time = sources.Time ?? TimeProvider.System;
        _logger = sources.Logger ?? NullLogger.Instance;
        _http = new HttpClient(sources.HttpHandler ?? new SocketsHttpHandler(), disposeHandler: sources.HttpHandler == null) { Timeout = sources.Timeout };
        _last = ReadFile() ?? Empty();
    }

    private static CliUpdatesResult Empty() => new();

    /// <summary>The last result (<c>GET /api/v1/qcli/updates</c>).</summary>
    public CliUpdatesResult Result()
    {
        lock (_lock)
        {
            var copy = _last.Clone();
            copy.Checking = _running != null;
            return copy;
        }
    }

    /// <summary>The check running now, null when none (tests await it).</summary>
    internal Task? CurrentCheck
    {
        get
        {
            lock (_lock) return _running;
        }
    }

    /// <summary>Checks now; a check already running is joined.</summary>
    public async Task<CliUpdatesResult> CheckAsync()
    {
        Task<CliUpdatesResult> running;
        lock (_lock) running = _running ??= Task.Run(RunAndClearAsync);
        await running;
        return Result();
    }

    private async Task<CliUpdatesResult> RunAndClearAsync()
    {
        try
        {
            return await RunAsync(_stopping.Token);
        }
        finally
        {
            lock (_lock) _running = null;
        }
    }

    /// <summary>
    /// Starts the periodic checks: the first after the initial delay (or when the last result is due), then every
    /// interval, through the jobs plugin when it is mounted (job <c>updates:check</c>), else a timer.
    /// </summary>
    public void Start()
    {
        CliUpdatesResult last;
        lock (_lock)
        {
            if (_started || !_options.Enabled) return;
            _started = true;
            last = _last;
        }
        var interval = _options.Interval;
        var first = _options.InitialDelay;
        if (last.CheckedAt != null && DateTimeOffset.TryParse(last.CheckedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var checkedAt))
        {
            var due = interval - (_time.GetUtcNow() - checkedAt);
            if (due > first) first = due;
        }
        AddTimer(first, Timeout.InfiniteTimeSpan);
        if (_sources.Jobs?.Invoke() is { } jobs && interval.Ticks % TimeSpan.TicksPerSecond == 0)
        {
            try
            {
                if (!jobs.IsRegistered(JobId))
                {
                    jobs.Register(new UpdateCheckJob(this), new CliJobOptions
                    {
                        Id = JobId,
                        Name = JobId,
                        Description = "Checks the marketplace and NuGet for newer versions of the plugins and the server",
                        Group = "system",
                        Tags = ["system", "updates"],
                        Interval = interval,
                        MaxRetries = 0,
                    });
                }
                return;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // The scheduler refused it: a timer instead
                _logger.LogWarning("[qcli-admin] the update check runs on a timer: {Error}", ex.Message);
            }
        }
        AddTimer(interval, interval);
    }

    private void AddTimer(TimeSpan due, TimeSpan period)
    {
        var timer = _time.CreateTimer(_ => _ = CheckQuietlyAsync(), null, due, period);
        lock (_lock)
        {
            // Stop() ran while Start() was adding its timers: keep none, Stop() already disposed the others
            if (_started)
            {
                _timers.Add(timer);
                return;
            }
        }
        timer.Dispose();
    }

    private async Task CheckQuietlyAsync()
    {
        try
        {
            await CheckAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[qcli-admin] the update check failed");
        }
    }

    /// <summary>Stops the periodic checks (the job of the jobs plugin stays registered; it stops with the server).</summary>
    public void Stop()
    {
        List<ITimer> timers;
        lock (_lock)
        {
            timers = [.. _timers];
            _timers.Clear();
            _started = false;
        }
        foreach (var t in timers) t.Dispose();
    }

    /// <summary>
    /// After an install, update or removal: drops the marketplace updates whose installed version caught up (or that
    /// were removed) and resolves their notifications, without asking the marketplace again.
    /// </summary>
    public void RefreshInstalled()
    {
        var dashboard = (_sources.DashboardInstalled?.Invoke() ?? []).GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.Last().Version);
        var server = (_sources.ServerInstalled?.Invoke() ?? []).GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.Last().Version);
        static bool Stale(CliUpdateItem item, Dictionary<string, string> installed, Func<string, string, int> compare) =>
            !installed.TryGetValue(item.Name, out var current) || item.Latest == null || compare(item.Latest, current) <= 0;
        List<CliUpdateItem> gone;
        lock (_lock)
        {
            gone = [.. _last.DashboardPlugins.Where(i => Stale(i, dashboard, DashboardMarketplace.CompareVersions)),
                .. _last.ServerPlugins.Where(i => Stale(i, server, NuGetPackage.CompareVersions))];
            if (gone.Count == 0) return;
            _last.DashboardPlugins = _last.DashboardPlugins.Where(i => !gone.Contains(i)).ToList();
            _last.ServerPlugins = _last.ServerPlugins.Where(i => !gone.Contains(i)).ToList();
            _last.Count = _last.CountUpdates();
            Save();
        }
        var notifications = _sources.Notifications?.Invoke();
        foreach (var item in gone) notifications?.Resolve(NotificationKey(item));
    }

    /// <summary><c>update:dashboard:&lt;name&gt;</c>, <c>update:server:&lt;name&gt;</c>, <c>update:code:&lt;kind&gt;:&lt;package&gt;</c>, <c>update:core:&lt;package&gt;</c>.</summary>
    public static string NotificationKey(CliUpdateItem item)
    {
        var prefix = CliNotificationKeys.UpdatePrefix;
        if (item.Kind == "core") return $"{prefix}core:{item.Package}";
        if (item.Source == CliUpdateSources.Code) return $"{prefix}code:{item.Kind}:{item.Package}";
        return $"{prefix}{item.Kind}:{item.Name}";
    }

    private async Task<CliUpdatesResult> RunAsync(CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        // The categories that answered: only their notifications are resolved
        var answered = new List<string>();
        var next = Empty();
        var prefix = CliNotificationKeys.UpdatePrefix;

        if (_sources.DashboardSearch is { } dashboardSearch)
        {
            try
            {
                foreach (var item in await ItemsAsync(dashboardSearch, cancellationToken))
                {
                    if (item["installed"] is not JsonObject installed || !IsTrue(item["updateAvailable"]) || Str(item, "name") is not { } name) continue;
                    var latest = Str(item, "version") ?? Str(item, "latest");
                    var current = Str(installed, "version");
                    var title = Str(item, "title") ?? name;
                    if (Str(installed, "source") == CliUpdateSources.Code)
                    {
                        next.CodePlugins.Add(new CliUpdateItem
                        {
                            Kind = "dashboard", Name = name, Package = name, Title = title, Icon = Str(item, "icon"), Current = current, Latest = latest,
                            Source = CliUpdateSources.Code, UpdateAvailable = true, CanUpdate = false, Reason = "update in code",
                            // A dashboard plugin is an npm package whatever the server's platform
                            Install = new() { ["node"] = $"npm install {name}{(latest != null ? $"@{latest}" : "")}" },
                        });
                    }
                    else
                    {
                        next.DashboardPlugins.Add(new CliUpdateItem
                        {
                            Kind = "dashboard", Name = name, Package = name, Title = title, Icon = Str(item, "icon"), Current = current, Latest = latest,
                            Source = CliUpdateSources.Marketplace, UpdateAvailable = true, CanUpdate = IsTrue(item["allowed"]), Reason = Str(item, "blockedReason"),
                        });
                    }
                }
                answered.AddRange([$"{prefix}dashboard:", $"{prefix}code:dashboard:"]);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors.Add($"dashboard plugins: {Reason(ex)}");
            }
        }

        if (_sources.ServerSearch is { } serverSearch)
        {
            try
            {
                foreach (var item in await ItemsAsync(serverSearch, cancellationToken))
                {
                    if (item["installed"] is not JsonObject installed || !IsTrue(item["updateAvailable"]) || item["platform"] is not JsonObject platform
                        || Str(item, "name") is not { } name || Str(platform, "package") is not { } package) continue;
                    var latest = Str(platform, "latest");
                    var current = Str(installed, "version");
                    var title = Str(item, "title") ?? name;
                    if (Str(installed, "source") == CliUpdateSources.Code)
                    {
                        next.CodePlugins.Add(new CliUpdateItem
                        {
                            Kind = "server", Name = name, Package = package, Title = title, Icon = Str(item, "icon"), Current = current, Latest = latest,
                            Source = CliUpdateSources.Code, UpdateAvailable = true, CanUpdate = false, Reason = "update in code",
                            Install = InstallCommands(item["platforms"], package, latest),
                        });
                    }
                    else
                    {
                        next.ServerPlugins.Add(new CliUpdateItem
                        {
                            Kind = "server", Name = name, Package = package, Title = title, Icon = Str(item, "icon"), Current = current, Latest = latest,
                            Source = CliUpdateSources.Marketplace, UpdateAvailable = true, CanUpdate = IsTrue(item["allowed"]), Reason = Str(item, "blockedReason"),
                        });
                    }
                }
                answered.AddRange([$"{prefix}server:", $"{prefix}code:server:"]);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors.Add($"server plugins: {Reason(ex)}");
            }
        }

        var running = _sources.ServerVersion();
        CliUpdatesResult last;
        lock (_lock) last = _last.Clone();
        try
        {
            var latest = await NuGetLatestAsync(cancellationToken);
            next.Server = new CliUpdateItem
            {
                Kind = "core", Name = ServerPackage, Package = ServerPackage, Title = "Server", Current = running, Latest = latest,
                Source = CliUpdateSources.NuGet, UpdateAvailable = Newer(latest, running), CanUpdate = false,
                Reason = "update the server packages in code (every Qodalis.Cli* package shares the version)",
                Install = new() { [ServerPluginMarketplace.Platform] = $"dotnet add package {ServerPackage}{(latest != null ? $" --version {latest}" : "")}" },
            };
            answered.Add($"{prefix}core:");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            errors.Add($"server package: {Reason(ex)}");
            next.Server = last.Server;
            if (next.Server != null)
            {
                next.Server.Current = running;
                next.Server.UpdateAvailable = Newer(next.Server.Latest, running);
            }
        }

        // A category that did not answer keeps its last items
        if (!answered.Contains($"{prefix}dashboard:"))
        {
            next.DashboardPlugins = last.DashboardPlugins;
            next.CodePlugins.AddRange(last.CodePlugins.Where(i => i.Kind == "dashboard"));
        }
        if (!answered.Contains($"{prefix}server:"))
        {
            next.ServerPlugins = last.ServerPlugins;
            next.CodePlugins.AddRange(last.CodePlugins.Where(i => i.Kind == "server"));
        }
        next.CheckedAt = _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        if (errors.Count > 0) next.Error = string.Join("; ", errors);
        next.Count = next.CountUpdates();
        lock (_lock)
        {
            _last = next;
            Save();
        }
        Publish(next, answered);
        return next;
    }

    private static string Reason(Exception ex) => ex switch
    {
        DashboardMarketplaceException d => d.Message,
        OperationCanceledException => "timed out",
        _ => ex.Message,
    };

    private static bool Newer(string? latest, string? current) =>
        latest != null && current != null && NuGetPackage.CompareVersions(latest, current) > 0;

    /// <summary>One notification per update; those of the categories that answered and no longer apply are resolved.</summary>
    private void Publish(CliUpdatesResult result, List<string> answered)
    {
        if (_sources.Notifications?.Invoke() is not { } notifications) return;
        var items = result.DashboardPlugins.Concat(result.ServerPlugins).Concat(result.CodePlugins)
            .Concat(result.Server is { UpdateAvailable: true } server ? [server] : []);
        var keys = new List<string>();
        foreach (var item in items)
        {
            var key = NotificationKey(item);
            keys.Add(key);
            var versions = $"{item.Current ?? "?"} → {item.Latest ?? "?"}";
            var title = item.Kind == "core" ? $"Server update available: {item.Latest}"
                : item.Source == CliUpdateSources.Code ? $"Update in code: {item.Title} {item.Latest}"
                : $"Update available: {item.Title} {item.Latest}";
            var message = item.Kind == "core" ? $"{item.Package} {versions}. Update the server packages in code and restart."
                : item.Source == CliUpdateSources.Code
                    ? $"{item.Package} {versions}. The host adds it in code: {item.Install?.Values.FirstOrDefault() ?? "update its package"}, then restart."
                    : $"{item.Package} {versions}.{(item.CanUpdate ? "" : $" {item.Reason}")}".Trim();
            try
            {
                notifications.Notify(new CliNotificationInput
                {
                    Key = key,
                    Kind = "update",
                    Severity = CliNotificationSeverities.Info,
                    Title = title.Length > 200 ? title[..200] : title,
                    Message = message.Length > 2000 ? message[..2000] : message,
                    Link = Link,
                    RequiredPermission = CliNotificationConstants.UpdatesReadPermission,
                    Data = new JsonObject
                    {
                        ["kind"] = item.Kind, ["name"] = item.Name, ["package"] = item.Package, ["current"] = item.Current, ["latest"] = item.Latest,
                        ["source"] = item.Source, ["canUpdate"] = item.CanUpdate,
                    },
                });
            }
            catch (ArgumentException)
            {
                // An item the notification rules refuse (a huge name): listed by GET /updates all the same
            }
        }
        foreach (var p in answered) notifications.ResolveMissing(p, keys);
    }

    /// <summary>
    /// The install commands per platform of a server plugin added in code: this platform's first, then the others' from
    /// the marketplace item (their own command when it gives one).
    /// </summary>
    internal static Dictionary<string, string> InstallCommands(JsonNode? platforms, string package, string? latest)
    {
        var result = new Dictionary<string, string> { [ServerPluginMarketplace.Platform] = Command(ServerPluginMarketplace.Platform, package, latest)! };
        var list = platforms switch
        {
            JsonArray a => a.OfType<JsonObject>().Select(p => (Str(p, "platform"), p)).ToList(),
            JsonObject m => m.Where(e => e.Value is JsonObject).Select(e => ((string?)e.Key, (JsonObject)e.Value!)).ToList(),
            _ => [],
        };
        foreach (var (platform, p) in list)
        {
            if (platform == null || platform == ServerPluginMarketplace.Platform || result.ContainsKey(platform) || Str(p, "package") is not { } pkg) continue;
            if ((Str(p, "install") ?? Command(platform, pkg, Str(p, "latest"))) is { } command) result[platform] = command;
        }
        return result;
    }

    private static string? Command(string platform, string package, string? latest) => platform switch
    {
        "dotnet" => $"dotnet add package {package}{(latest != null ? $" --version {latest}" : "")}",
        "node" => $"npm install {package}{(latest != null ? $"@{latest}" : "")}",
        "python" => $"pip install --upgrade {package}{(latest != null ? $"=={latest}" : "")}",
        _ => null,
    };

    private static async Task<List<JsonObject>> ItemsAsync(Func<int, int, CancellationToken, Task<JsonObject>> search, CancellationToken cancellationToken)
    {
        var all = new List<JsonObject>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var body = await search(page, PageSize, cancellationToken);
            var items = (body["items"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            all.AddRange(items);
            var total = body["total"] is JsonValue v && v.TryGetValue<int>(out var t) ? t : all.Count;
            if (items.Count == 0 || all.Count >= total) break;
        }
        return all;
    }

    /// <summary>The newest version of the server package on the NuGet flat container (<c>&lt;feed&gt;/qodalis.cli/index.json</c>); null when it has none.</summary>
    private async Task<string?> NuGetLatestAsync(CancellationToken cancellationToken)
    {
        var url = $"{_sources.NuGetFeed.TrimEnd('/')}/{ServerPackage.ToLowerInvariant()}/index.json";
        JsonNode? body;
        try
        {
            using var res = await _http.GetAsync(url, cancellationToken);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException("NuGet feed unreachable");
            body = JsonNode.Parse(await res.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("NuGet feed unreachable");
        }
        return (body?["versions"] as JsonArray)?.OfType<JsonValue>()
            .Where(v => v.GetValueKind() == JsonValueKind.String).Select(v => v.GetValue<string>()).Where(NuGetPackage.IsVersion)
            .OrderByDescending(v => v, Comparer<string>.Create(NuGetPackage.CompareVersions)).FirstOrDefault();
    }

    private static string? Str(JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Trim() is { Length: > 0 } s ? s : null;

    private static bool IsTrue(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    // ---------------------------------------------------------------- file

    private CliUpdatesResult? ReadFile()
    {
        if (_sources.File is not { } file || !System.IO.File.Exists(file)) return null;
        try
        {
            var result = JsonSerializer.Deserialize<CliUpdatesResult>(System.IO.File.ReadAllText(file));
            if (result == null) return null;
            result.Checking = false;
            result.DashboardPlugins ??= [];
            result.ServerPlugins ??= [];
            result.CodePlugins ??= [];
            // The running version may have changed since (an update applied in code)
            if (result.Server != null)
            {
                var current = _sources.ServerVersion();
                result.Server.Current = current;
                result.Server.UpdateAvailable = Newer(result.Server.Latest, current);
            }
            result.Count = result.CountUpdates();
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("[qcli-admin] reading {File} failed, starting without the last update check: {Error}", file, ex.Message);
            return null;
        }
    }

    /// <summary>Writes the last result (without <c>checking</c>) atomically. Call with the lock held.</summary>
    private void Save()
    {
        if (_sources.File is not { } file) return;
        var tmp = $"{file}.{Environment.ProcessId}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            var node = JsonSerializer.SerializeToNode(_last)!.AsObject();
            node.Remove("checking");
            System.IO.File.WriteAllText(tmp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            System.IO.File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unwritable data directory: the result stays in memory
            _logger.LogWarning("[qcli-admin] saving {File} failed: {Error}", file, ex.Message);
            try
            {
                System.IO.File.Delete(tmp);
            }
            catch (Exception)
            {
                // nothing written
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _stopping.Cancel();
        _stopping.Dispose();
        _http.Dispose();
    }

    /// <summary>The job of the jobs plugin: one check, a failed one (the marketplace or NuGet unreachable) reported as failed.</summary>
    private sealed class UpdateCheckJob(UpdateChecker checker) : ICliJob
    {
        public async Task ExecuteAsync(ICliJobExecutionContext context, CancellationToken cancellationToken)
        {
            var result = await checker.CheckAsync();
            context.Logger.Info($"{result.Count} update(s) available{(result.Error != null ? $" ({result.Error})" : "")}");
            if (result.Error != null) throw new InvalidOperationException(result.Error);
        }
    }
}
