using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Abstractions.Restart;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugins;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>An installed server plugin as <c>GET /installed</c> lists it.</summary>
public sealed class InstalledServerPlugin
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("package")] public required string Package { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }

    [JsonPropertyName("icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Icon { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Description { get; init; }

    [JsonPropertyName("installedAt")] public required string InstalledAt { get; init; }
    [JsonPropertyName("installedBy")] public required string InstalledBy { get; init; }

    /// <summary>
    /// <c>loaded</c>: running this version; <c>needs-configuration</c>: running, a required setting has no value;
    /// <c>pending</c>: installed or updated, running after a restart; <c>failed</c>: the last start could not load it
    /// (<see cref="Error"/>); <c>skipped</c>: the host installed the same package; <c>removed</c>: removed, still running
    /// until a restart.
    /// </summary>
    [JsonPropertyName("status")] public required string Status { get; init; }

    /// <summary>The version this process runs, null when none.</summary>
    [JsonPropertyName("loadedVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? LoadedVersion { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    /// <summary>While <c>needs-configuration</c>: the required settings without a value (<c>&lt;provider&gt;.&lt;section&gt;.&lt;key&gt;</c>).</summary>
    [JsonPropertyName("missing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Missing { get; init; }

    /// <summary>The config provider of its settings (<c>config set &lt;settings&gt;.settings.&lt;key&gt;</c>), once it ran.</summary>
    [JsonPropertyName("settings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Settings { get; init; }

    [JsonPropertyName("updateAvailable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? UpdateAvailable { get; init; }

    // What an install, update or removal answers (202)

    [JsonPropertyName("restartRequired")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RestartRequired { get; init; }

    [JsonPropertyName("changed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Changed { get; init; }

    [JsonPropertyName("previousVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousVersion { get; init; }

    internal InstalledServerPlugin With(bool? restartRequired = null, bool? changed = null, string? previousVersion = null,
        string? status = null, bool? updateAvailable = null) => new()
    {
        Name = Name, Package = Package, Version = Version, Title = Title, Icon = Icon, Description = Description,
        InstalledAt = InstalledAt, InstalledBy = InstalledBy, Status = status ?? Status, LoadedVersion = LoadedVersion, Error = Error, Missing = Missing, Settings = Settings,
        UpdateAvailable = updateAvailable ?? UpdateAvailable, RestartRequired = restartRequired ?? RestartRequired,
        Changed = changed ?? Changed, PreviousVersion = previousVersion ?? PreviousVersion,
    };
}

/// <summary>
/// Server plugins from the marketplace (<c>kind=server</c>), the .NET package only (<c>platforms.dotnet</c>): browses
/// them, and installs, updates and removes NuGet packages in <c>&lt;data dir&gt;/server-plugins/&lt;package&gt;/&lt;version&gt;/</c>
/// (the .nupkg downloaded from the NuGet feed and checked against the marketplace's sha512; its <c>lib/&lt;tfm&gt;</c>
/// assemblies and those of the dependencies the server does not have extracted there), records them in
/// <c>installed.json</c> and marks a restart as required: they are loaded at the next start.
/// </summary>
public sealed partial class ServerPluginMarketplace : IDisposable
{
    /// <summary>This server's platform in the marketplace (<c>platforms.dotnet</c>).</summary>
    public const string Platform = "dotnet";

    /// <summary>The permission to install, update and remove server plugins.</summary>
    public const string InstallPermission = CliRoutePolicy.PluginsInstall;

    /// <summary>nuget.org's flat container.</summary>
    public const string DefaultNuGetFeed = "https://api.nuget.org/v3-flatcontainer";

    /// <summary>The largest .nupkg accepted (each).</summary>
    public const long MaxPackageBytes = 50L * 1024 * 1024;

    /// <summary>The most packages one install downloads (the plugin and its dependencies).</summary>
    public const int MaxPackages = 50;

    /// <summary>The server package whose version this server reports (every server package shares one version).</summary>
    public const string ServerCorePackage = "Qodalis.Cli";

    /// <summary>The packages a plugin's dependency range on the server is read from.</summary>
    public static readonly IReadOnlyList<string> ServerCorePackages = ["Qodalis.Cli", "Qodalis.Cli.Abstractions"];

    /// <summary>Published when a server plugin is installed, updated or removed (audience <c>admin:access</c>).</summary>
    public const string ChangedEvent = "server:plugins-changed";

    /// <summary>The API's route.</summary>
    public const string ApiPath = "/api/v1/qcli/server/marketplace";

    private readonly CliServerPlugins _plugins;
    private readonly Func<IReadOnlyList<CliInstalledPlugin>> _installed;
    private readonly Func<ICliServerRestart?> _restart;
    private readonly Func<IAuditStore?> _audit;
    private readonly Func<ICliEventBus?> _bus;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _changes = new(1, 1);

    public ServerPluginMarketplace(
        DashboardMarketplaceOptions options,
        CliServerPlugins plugins,
        Func<IReadOnlyList<CliInstalledPlugin>> installed,
        Func<ICliServerRestart?>? restart = null,
        Func<IAuditStore?>? audit = null,
        Func<ICliEventBus?>? bus = null,
        ILogger? logger = null,
        TimeProvider? time = null)
    {
        options.Validate();
        Url = (options.Url ?? DashboardMarketplaceOptions.DefaultUrl).Trim().TrimEnd('/');
        Install = options.Install;
        NuGetFeed = (options.NuGetFeed ?? DefaultNuGetFeed).Trim().TrimEnd('/');
        TrustedDependencies = (options.TrustedDependencies ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
        _plugins = plugins;
        _installed = installed;
        _restart = restart ?? (() => null);
        _audit = audit ?? (() => null);
        _bus = bus ?? (() => null);
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _http = new HttpClient(options.HttpHandler ?? new SocketsHttpHandler(), disposeHandler: options.HttpHandler == null)
        {
            // Downloads get at least a minute; the marketplace's JSON the configured timeout
            Timeout = options.Timeout > TimeSpan.FromMinutes(1) ? options.Timeout : TimeSpan.FromMinutes(1),
        };
        _timeout = options.Timeout;
    }

    private readonly TimeSpan _timeout;

    public string Url { get; }

    /// <summary><c>verified</c>, <c>listed</c> or <c>off</c>.</summary>
    public string Install { get; }

    /// <summary>The NuGet flat container packages are downloaded from.</summary>
    public string NuGetFeed { get; }

    /// <summary>Dependencies installed without a hash in the marketplace record (ids or <c>Prefix.*</c>).</summary>
    public IReadOnlyList<string> TrustedDependencies { get; }

    private bool Trusted(string id) => TrustedDependencies.Any(t => t.EndsWith(".*", StringComparison.Ordinal)
        ? id.StartsWith(t[..^1], StringComparison.OrdinalIgnoreCase)
        : string.Equals(t, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The dependencies the marketplace pins for a version, by id: <c>dependencies: [{ id, version, integrity }]</c>
    /// (or <c>package</c> for <c>id</c>), or <c>{ id: { version, integrity } }</c>; entries without a sha512 are left out.
    /// </summary>
    internal static Dictionary<string, (string Version, string Integrity)> PinnedDependencies(JsonObject version)
    {
        var pinned = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        void Add(string? id, JsonNode? node)
        {
            var v = Str(node, "version");
            var integrity = Str(node, "integrity");
            if (id != null && NuGetPackage.IsVersion(v) && integrity != null && Sha512().IsMatch(integrity)) pinned[id] = (v!, integrity);
        }
        switch (version["dependencies"])
        {
            case JsonArray list:
                foreach (var d in list.OfType<JsonObject>()) Add(Str(d, "id") ?? Str(d, "package"), d);
                break;
            case JsonObject map:
                foreach (var (id, d) in map) Add(id, d);
                break;
        }
        return pinned;
    }

    /// <summary>The server's runtime plugins.</summary>
    public CliServerPlugins Plugins => _plugins;

    /// <summary>The running server's version (<c>Qodalis.Cli</c>).</summary>
    public string? ServerVersion => _installed().FirstOrDefault(p => p.Id == ServerCorePackage)?.Version;

    /// <summary><c>GET /</c>: the marketplace, the policy, this platform and the server's version.</summary>
    public JsonObject Info() => new()
    {
        ["marketplaceUrl"] = Url,
        ["install"] = Install,
        ["platform"] = Platform,
        ["serverVersion"] = ServerVersion,
        ["enabled"] = _plugins.Enabled,
    };

    // ---------------------------------------------------------------- JSON and ranges

    private static string? Str(JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static bool? Bool(JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;

    private static bool IsTrue(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    [GeneratedRegex(@"^sha512-[A-Za-z0-9+/]+={0,2}$")]
    private static partial Regex Sha512();

    /// <summary><c>sha512-&lt;base64&gt;</c> of the bytes.</summary>
    public static string Sha512Integrity(ReadOnlySpan<byte> bytes) => $"sha512-{Convert.ToBase64String(SHA512.HashData(bytes))}";

    [GeneratedRegex(@"^v?(\d+)(?:\.(\d+|[xX*]))?(?:\.(\d+|[xX*]))?(?:-([0-9A-Za-z-.]+))?$")]
    private static partial Regex Partial();

    [GeneratedRegex(@"^(\^|~|>=|<=|>|<|=)?(.+)$")]
    private static partial Regex Comparator();

    /// <summary>
    /// True when <paramref name="version"/> is in the range: a NuGet range (<c>[1.0.0-beta.29, 2.0.0)</c>) or an npm one
    /// (<c>^1.0.0-beta.29</c>, <c>&gt;=1.0.0 &lt;2</c>, <c>1.x || 2.x</c>), prereleases compared like releases. An empty
    /// range accepts every version; one it cannot read accepts none.
    /// </summary>
    public static bool SatisfiesServerRange(string version, string? range)
    {
        var text = (range ?? string.Empty).Trim();
        if (text.Length == 0 || text is "*" or "latest") return true;
        if (text[0] is '[' or '(') return NuGetPackage.SatisfiesNuGetRange(version, text);
        if (!NuGetPackage.IsVersion(version)) return false;
        return text.Split("||").Any(set =>
        {
            var normalized = Regex.Replace(set.Trim(), @"(>=|<=|>|<|=|\^|~)\s+", "$1");
            var hyphen = Regex.Match(normalized, @"^(\S+)\s+-\s+(\S+)$");
            var tests = Comparators(hyphen.Success ? $">={hyphen.Groups[1].Value} <={hyphen.Groups[2].Value}" : normalized);
            return tests != null && tests.All(t => t(version));
        });
    }

    private static List<Func<string, bool>>? Comparators(string set)
    {
        var tests = new List<Func<string, bool>>();
        static int Cmp(string a, string b) => NuGetPackage.CompareVersions(a, b);
        foreach (var token in set.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token is "*" or "x" or "X") continue;
            var m = Comparator().Match(token);
            var op = m.Groups[1].Success ? m.Groups[1].Value : string.Empty;
            var p = Partial().Match(m.Groups[2].Value);
            if (!p.Success) return null;
            int? Num(int i) => p.Groups[i].Success && !Regex.IsMatch(p.Groups[i].Value, "^[xX*]$")
                ? int.Parse(p.Groups[i].Value, CultureInfo.InvariantCulture) : null;
            var major = Num(1)!.Value;
            var minor = Num(2);
            var patch = minor == null ? null : Num(3);
            var pre = p.Groups[4].Success ? p.Groups[4].Value : null;
            var low = $"{major}.{minor ?? 0}.{patch ?? 0}{(pre != null ? "-" + pre : string.Empty)}";
            string? high = null;
            if (op == "^")
            {
                if (major > 0 || minor == null) high = $"{major + 1}.0.0-0";
                else if (minor > 0 || patch == null) high = $"0.{minor + 1}.0-0";
                else high = $"0.0.{patch + 1}-0";
            }
            else if (op == "~")
            {
                high = minor == null ? $"{major + 1}.0.0-0" : $"{major}.{minor + 1}.0-0";
            }
            else if (op is "" or "=")
            {
                if (minor == null) high = $"{major + 1}.0.0-0";
                else if (patch == null) high = $"{major}.{minor + 1}.0-0";
                else
                {
                    tests.Add(v => Cmp(v, low) == 0);
                    continue;
                }
            }
            switch (op)
            {
                case ">": tests.Add(v => Cmp(v, low) > 0); break;
                case "<": tests.Add(v => Cmp(v, low) < 0); break;
                case "<=": tests.Add(v => Cmp(v, low) <= 0); break;
                default: tests.Add(v => Cmp(v, low) >= 0); break;
            }
            if (high != null)
            {
                var h = high;
                tests.Add(v => Cmp(v, h) < 0);
            }
        }
        return tests;
    }

    /// <summary>The platforms of an item: <c>{ dotnet: {...}, node: {...} }</c> from either form (map or list).</summary>
    private static Dictionary<string, JsonObject> PlatformsOf(JsonObject item)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        switch (item["platforms"])
        {
            case JsonArray list:
                foreach (var p in list.OfType<JsonObject>())
                    if (Str(p, "platform") is { } platform && Str(p, "package") != null) result[platform] = p;
                break;
            case JsonObject map:
                foreach (var (key, value) in map)
                    if (value is JsonObject p && Str(p, "package") != null) result[key] = p;
                break;
        }
        return result;
    }

    /// <summary>This platform's versions: the plugin's flat list (each with its <c>platform</c>), else the platform's own.</summary>
    private static List<JsonObject>? PlatformVersions(JsonObject item, JsonObject own)
    {
        if (item["versions"] is JsonArray all)
            return all.OfType<JsonObject>().Where(v => (Str(v, "platform") ?? Platform) == Platform).ToList();
        return own["versions"] is JsonArray versions ? versions.OfType<JsonObject>().ToList() : null;
    }

    /// <summary>The listed versions of this platform, newest first (its <c>latest</c> alone for a list item).</summary>
    private static List<JsonObject> ListedVersions(JsonObject item, JsonObject own)
    {
        var versions = PlatformVersions(item, own)
            ?? (Str(own, "latest") is { } latest ? [new JsonObject { ["version"] = latest, ["status"] = "listed", ["serverRange"] = Str(own, "serverRange") }] : []);
        return versions.Where(v => NuGetPackage.IsVersion(Str(v, "version")) && (Str(v, "status") ?? "listed") == "listed")
            .OrderByDescending(v => Str(v, "version")!, Comparer<string>.Create(NuGetPackage.CompareVersions)).ToList();
    }

    /// <summary>Whether the plugin runs without the host's code: the version's, else the platform's, else the item's <c>autoInstall</c>.</summary>
    private static (bool AutoInstall, string? Reason) AutoInstallOf(JsonObject item, JsonObject? own, JsonObject? version = null)
    {
        foreach (var source in new[] { version, own, item })
        {
            if (Bool(source, "autoInstall") is { } auto) return auto ? (true, null) : (false, Str(source, "autoInstallReason"));
        }
        return (true, null);
    }

    private static string? RangeOf(JsonObject? version, JsonObject own) => Str(version, "serverRange") ?? Str(own, "serverRange");

    // ---------------------------------------------------------------- browsing

    private string PlatformQuery()
    {
        var qs = $"kind=server&platform={Platform}";
        return ServerVersion is { } server ? $"{qs}&serverVersion={Uri.EscapeDataString(server)}" : qs;
    }

    /// <summary><c>GET /plugins</c>: the marketplace's server plugins, annotated. Throws 502 when it is unreachable.</summary>
    public async Task<JsonObject> SearchAsync(string? query = null, int? page = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var qs = PlatformQuery();
        var q = query?.Trim();
        if (!string.IsNullOrEmpty(q)) qs += $"&query={Uri.EscapeDataString(q.Length > 200 ? q[..200] : q)}";
        if (page != null) qs += $"&page={page}";
        if (pageSize != null) qs += $"&pageSize={pageSize}";
        var body = await GetJsonAsync($"{Url}{DashboardMarketplace.MarketplaceApiPath}/plugins?{qs}", cancellationToken) as JsonObject;
        if (body?["items"] is not JsonArray all) throw new DashboardMarketplaceException(502, "marketplace unreachable");
        var items = all.OfType<JsonObject>().Where(i => CliServerPlugins.IsValidName(Str(i, "name")) && Str(i, "kind") == "server").ToList();
        var filtered = items.Count == all.Count;
        var annotated = new JsonArray();
        foreach (var item in items)
        {
            var copy = item.DeepClone().AsObject();
            Annotate(copy, item);
            annotated.Add(copy);
        }
        static bool IsNum(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number;
        return new JsonObject
        {
            ["items"] = annotated,
            ["total"] = filtered && IsNum(body["total"]) ? body["total"]!.DeepClone() : items.Count,
            ["page"] = IsNum(body["page"]) ? body["page"]!.DeepClone() : page ?? 1,
            ["pageSize"] = IsNum(body["pageSize"]) ? body["pageSize"]!.DeepClone() : pageSize ?? items.Count,
        };
    }

    /// <summary><c>GET /plugins/{name}</c>: one plugin with its platforms and versions (this platform's with <c>allowed</c> and <c>blockedReason</c>).</summary>
    public async Task<JsonObject> DetailsAsync(string? name, CancellationToken cancellationToken = default)
    {
        var plugin = await FetchPluginAsync(name, cancellationToken);
        var copy = plugin.DeepClone().AsObject();
        var own = PlatformsOf(plugin).GetValueOrDefault(Platform);
        if (own != null && copy["versions"] is JsonArray versions)
        {
            foreach (var v in versions.OfType<JsonObject>())
            {
                if ((Str(v, "platform") ?? Platform) != Platform) continue;
                var reason = VersionBlockedReason(plugin, own, v);
                v["allowed"] = reason == null;
                if (reason != null) v["blockedReason"] = reason;
            }
        }
        Annotate(copy, plugin);
        return copy;
    }

    private void Annotate(JsonObject target, JsonObject item)
    {
        var platforms = PlatformsOf(item);
        var own = platforms.GetValueOrDefault(Platform);
        var record = RecordOf(Str(item, "name")!);
        var auto = AutoInstallOf(item, own);
        JsonObject? platform = null;
        bool compatible = false;
        string? reason = null;
        string? latest = null;
        if (own != null && PlatformVersions(item, own) == null)
        {
            // A list item: the marketplace says whether it runs here (it was given this server's version)
            compatible = Bool(item, "compatible") != false;
            latest = Str(item, "latest") ?? (compatible ? Str(own, "latest") : null);
            if (!compatible) reason = Str(item, "reason") ?? "not compatible with this server";
        }
        else if (own != null)
        {
            var listed = ListedVersions(item, own);
            var server = ServerVersion;
            var fitting = listed.FirstOrDefault(v => server == null || SatisfiesServerRange(server, RangeOf(v, own)));
            compatible = fitting != null;
            latest = Str(fitting, "version");
            if (!compatible)
                reason = listed.Count > 0 ? $"needs server {RangeOf(listed[0], own)} (this server has {server})" : $"no listed version for {Platform}";
        }
        if (own != null)
        {
            platform = new JsonObject
            {
                ["package"] = Str(own, "package"),
                ["latest"] = latest,
                ["installed"] = record?.Version,
                ["compatible"] = compatible,
            };
            if (reason != null) platform["reason"] = reason;
        }
        var blocked = BlockedReason(item, own, own == null ? null : (compatible, reason), auto);
        target["platform"] = platform;
        target["otherPlatforms"] = new JsonArray(platforms.Keys.Where(p => p != Platform).Select(p => (JsonNode?)p).ToArray());
        target["autoInstall"] = auto.AutoInstall;
        if (auto.Reason != null) target["autoInstallReason"] = auto.Reason;
        target["updateAvailable"] = record != null && latest != null && NuGetPackage.CompareVersions(latest, record.Version) > 0;
        target["allowed"] = blocked == null;
        if (blocked != null) target["blockedReason"] = blocked;
    }

    /// <summary><c>server plugins disabled</c>, <c>install disabled</c>, <c>unverified publisher</c>, <c>not available for dotnet</c>, <c>needs setup in code</c>, the range.</summary>
    private string? BlockedReason(JsonObject item, JsonObject? own, (bool Compatible, string? Reason)? platform, (bool AutoInstall, string? Reason) auto)
    {
        if (!_plugins.Enabled) return "server plugins disabled";
        if (Install == "off") return "install disabled";
        if (Install == "verified" && !IsTrue(item["publisher"]?["verified"])) return "unverified publisher";
        if (own == null) return $"not available for {Platform}";
        if (!auto.AutoInstall) return "needs setup in code";
        if (platform is { Compatible: false } p) return p.Reason;
        return null;
    }

    private string? VersionBlockedReason(JsonObject plugin, JsonObject own, JsonObject v)
    {
        var server = ServerVersion;
        var compatible = server == null || SatisfiesServerRange(server, RangeOf(v, own));
        var reason = BlockedReason(plugin, own, (compatible, compatible ? null : $"needs server {RangeOf(v, own)} (this server has {server})"),
            AutoInstallOf(plugin, own, v));
        if (reason != null) return reason;
        if ((Str(v, "status") ?? "listed") != "listed") return $"version {Str(v, "status")}";
        return null;
    }

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        var what = url.StartsWith(Url, StringComparison.Ordinal) ? "marketplace unreachable" : "NuGet feed unreachable";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) throw new DashboardMarketplaceException(502, what);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (Exception ex) when (ex is not DashboardMarketplaceException && !cancellationToken.IsCancellationRequested)
        {
            throw new DashboardMarketplaceException(502, what);
        }
    }

    private async Task<JsonObject> FetchPluginAsync(string? name, CancellationToken cancellationToken)
    {
        if (!CliServerPlugins.IsValidName(name)) throw new DashboardMarketplaceException(400, "invalid plugin name");
        if (await GetJsonAsync($"{Url}{DashboardMarketplace.MarketplaceApiPath}/plugins/{Uri.EscapeDataString(name!)}?{PlatformQuery()}", cancellationToken)
            is not JsonObject plugin)
            throw new DashboardMarketplaceException(404, $"plugin not found: {name}");
        if (Str(plugin, "name") != name) throw new DashboardMarketplaceException(502, "marketplace unreachable");
        return plugin;
    }

    // ---------------------------------------------------------------- installed

    private CliServerPluginRecord? RecordOf(string name) => _plugins.Records().FirstOrDefault(r => r.Name == name);

    private static bool Running(CliServerPluginState s) => s.Status is CliServerPluginStatuses.Loaded or CliServerPluginStatuses.NeedsConfiguration;

    /// <summary>This process's states of installed plugins (not those added in code).</summary>
    private IEnumerable<CliServerPluginState> MarketplaceStates() => _plugins.States().Where(s => s.Source == CliServerPluginSources.Marketplace);

    private CliServerPluginState? LoadedOf(string name) => MarketplaceStates().FirstOrDefault(s => s.Name == name && Running(s));

    /// <summary>One installed plugin with how it runs in this process.</summary>
    public InstalledServerPlugin View(CliServerPluginRecord record)
    {
        var state = MarketplaceStates().FirstOrDefault(s => s.Name == record.Name);
        var loaded = state != null && Running(state) ? state.Version : null;
        string status;
        var error = record.Error;
        if (state != null && state.Version == record.Version && (state.Status != CliServerPluginStatuses.Loaded))
        {
            status = state.Status;
            error = state.Error ?? error;
        }
        else
        {
            status = loaded == record.Version ? CliServerPluginStatuses.Loaded : "pending";
        }
        return new InstalledServerPlugin
        {
            Name = record.Name,
            Package = record.Package,
            Version = record.Version,
            Title = record.Title ?? record.Name,
            Icon = record.Icon,
            Description = record.Description,
            InstalledAt = record.InstalledAt,
            InstalledBy = record.InstalledBy,
            Status = status,
            LoadedVersion = loaded,
            Error = status is CliServerPluginStatuses.Loaded or "pending" ? null : error,
            Missing = status == CliServerPluginStatuses.NeedsConfiguration ? state?.Missing : null,
            Settings = state?.Settings ?? record.Settings,
        };
    }

    /// <summary><c>GET /installed</c>: the installed plugins, then the removed ones still running; <c>updateAvailable</c> when the marketplace answers.</summary>
    public async Task<IReadOnlyList<InstalledServerPlugin>> InstalledAsync(bool checkUpdates = false, CancellationToken cancellationToken = default)
    {
        await _plugins.CheckSettingsAsync();
        var records = _plugins.Records();
        var views = records.Select(View).ToList();
        foreach (var s in MarketplaceStates())
        {
            if (!Running(s) || records.Any(r => r.Name == s.Name)) continue;
            views.Add(new InstalledServerPlugin
            {
                Name = s.Name, Package = s.Package, Version = s.Version, Title = s.Name, InstalledAt = string.Empty, InstalledBy = string.Empty,
                Status = "removed", LoadedVersion = s.Version,
            });
        }
        if (!checkUpdates) return views;
        var checkedViews = await Task.WhenAll(views.Select(async v =>
        {
            if (v.Status == "removed") return v;
            try
            {
                var plugin = await FetchPluginAsync(v.Name, cancellationToken);
                var copy = new JsonObject();
                Annotate(copy, plugin);
                var latest = Str(copy["platform"], "latest");
                return v.With(updateAvailable: latest != null && NuGetPackage.CompareVersions(latest, v.Version) > 0);
            }
            catch (DashboardMarketplaceException)
            {
                return v;
            }
        }));
        return checkedViews;
    }

    // ---------------------------------------------------------------- changes

    private async Task<T> ExclusiveAsync<T>(Func<Task<T>> action)
    {
        await _changes.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary><c>POST /installed</c>: installs this platform's package of <paramref name="name"/> (the newest compatible version, or <paramref name="version"/>).</summary>
    public Task<InstalledServerPlugin> InstallPluginAsync(string? name, string? version, DashboardMarketplaceActor actor, CancellationToken cancellationToken = default) =>
        ExclusiveAsync(async () =>
        {
            var (plugin, own, target) = await PrepareAsync(name, version, cancellationToken);
            var pluginName = Str(plugin, "name")!;
            if (RecordOf(pluginName) is { } existing) throw new DashboardMarketplaceException(409, $"already installed: {pluginName}@{existing.Version}");
            var record = await InstallPackageAsync(plugin, own, target, actor, cancellationToken);
            _plugins.Save([.. _plugins.Records().Where(r => r.Name != record.Name), record]);
            var restartRequired = SyncRestart(record.Name, $"Install {record.Name} {record.Version}");
            await ChangedAsync("install", record, actor, null);
            return View(record).With(restartRequired: restartRequired);
        });

    /// <summary><c>PUT /installed/{name}</c>: updates to <paramref name="version"/> (default the newest compatible one); <c>changed: false</c> when it already is.</summary>
    public Task<InstalledServerPlugin> UpdatePluginAsync(string? name, string? version, DashboardMarketplaceActor actor, CancellationToken cancellationToken = default) =>
        ExclusiveAsync(async () =>
        {
            if (!CliServerPlugins.IsValidName(name)) throw new DashboardMarketplaceException(400, "invalid plugin name");
            var current = RecordOf(name!) ?? throw new DashboardMarketplaceException(404, $"not installed: {name}");
            var (plugin, own, target) = await PrepareAsync(name, version, cancellationToken);
            var package = Str(own, "package")!;
            if (!string.Equals(package, current.Package, StringComparison.OrdinalIgnoreCase))
                throw new DashboardMarketplaceException(409, $"the marketplace's package is {package}, {current.Package} is installed: remove it first");
            var targetVersion = Str(target, "version")!;
            if (string.Equals(targetVersion, current.Version, StringComparison.OrdinalIgnoreCase))
                return View(current).With(restartRequired: RestartPending(current.Name), changed: false, previousVersion: current.Version);
            var record = await InstallPackageAsync(plugin, own, target, actor, cancellationToken);
            _plugins.Save(_plugins.Records().Select(r => r.Name == record.Name ? record : r).ToList());
            // The old version's files stay while this process may run them: removed unless they are loaded
            if (LoadedOf(current.Name)?.Version != current.Version) DeleteVersion(current.Package, current.Version);
            var restartRequired = SyncRestart(record.Name, $"Update {record.Name} {current.Version} -> {record.Version}");
            await ChangedAsync("update", record, actor, current.Version);
            return View(record).With(restartRequired: restartRequired, changed: true, previousVersion: current.Version);
        });

    /// <summary><c>DELETE /installed/{name}</c>: uninstalls the package; it keeps running until a restart.</summary>
    public Task<InstalledServerPlugin> RemovePluginAsync(string? name, DashboardMarketplaceActor actor) =>
        ExclusiveAsync(async () =>
        {
            if (!CliServerPlugins.IsValidName(name)) throw new DashboardMarketplaceException(400, "invalid plugin name");
            if (!_plugins.Enabled) throw new DashboardMarketplaceException(403, "server plugins disabled");
            var current = RecordOf(name!) ?? throw new DashboardMarketplaceException(404, $"not installed: {name}");
            _plugins.Save(_plugins.Records().Where(r => r.Name != current.Name).ToList());
            var loaded = LoadedOf(current.Name);
            if (loaded == null) DeleteVersion(current.Package, current.Version);
            var restartRequired = SyncRestart(current.Name, $"Remove {current.Name} {current.Version}");
            await ChangedAsync("remove", current, actor, null);
            var view = View(current);
            return view.With(restartRequired: restartRequired, status: loaded != null ? "removed" : view.Status);
        });

    /// <summary>Deletes a version's folder (and the package's when empty); errors are logged (files in use on Windows).</summary>
    private void DeleteVersion(string package, string version)
    {
        try
        {
            var dir = _plugins.VersionDir(package, version);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            var parent = Path.GetDirectoryName(dir)!;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning("[qcli-admin] could not delete {Package} {Version}: {Error}", package, version, ex.Message);
        }
    }

    /// <summary>The plugin, this platform's package and the version to install, after the policy, platform, setup and range checks.</summary>
    private async Task<(JsonObject Plugin, JsonObject Own, JsonObject Target)> PrepareAsync(string? name, string? version, CancellationToken cancellationToken)
    {
        if (!CliServerPlugins.IsValidName(name)) throw new DashboardMarketplaceException(400, "invalid plugin name");
        if (version != null && !NuGetPackage.IsVersion(version)) throw new DashboardMarketplaceException(400, "invalid version");
        if (!_plugins.Enabled) throw new DashboardMarketplaceException(403, "server plugins disabled");
        var plugin = await FetchPluginAsync(name, cancellationToken);
        if (Str(plugin, "kind") != "server") throw new DashboardMarketplaceException(400, $"not a server plugin: {name}");
        if (Install == "off") throw new DashboardMarketplaceException(403, "install disabled");
        if (Install == "verified" && !IsTrue(plugin["publisher"]?["verified"])) throw new DashboardMarketplaceException(403, "publisher not verified");
        var own = PlatformsOf(plugin).GetValueOrDefault(Platform) ?? throw new DashboardMarketplaceException(400, $"not available for {Platform}: {name}");
        if (!CliServerPlugins.IsValidPackage(Str(own, "package"))) throw new DashboardMarketplaceException(502, "invalid marketplace record: package");
        var listed = ListedVersions(plugin, own);
        var server = ServerVersion;
        JsonObject? target;
        if (version != null)
        {
            target = listed.FirstOrDefault(v => string.Equals(Str(v, "version"), version, StringComparison.OrdinalIgnoreCase))
                ?? throw new DashboardMarketplaceException(404, $"version not found: {name}@{version}");
        }
        else
        {
            target = listed.FirstOrDefault(v => server == null || SatisfiesServerRange(server, RangeOf(v, own))) ?? listed.FirstOrDefault()
                ?? throw new DashboardMarketplaceException(404, $"no installable version: {name}");
        }
        var auto = AutoInstallOf(plugin, own, target);
        if (!auto.AutoInstall) throw new DashboardMarketplaceException(400, $"needs setup in code{(auto.Reason != null ? $": {auto.Reason}" : string.Empty)}");
        var range = RangeOf(target, own);
        if (server != null && !SatisfiesServerRange(server, range))
            throw new DashboardMarketplaceException(400, $"incompatible: {Str(own, "package")}@{Str(target, "version")} needs server {range} (this server has {server})");
        var package = Str(own, "package")!;
        if (_installed().Any(p => string.Equals(p.Id, package, StringComparison.OrdinalIgnoreCase) && p.Source != CliInstalledPlugin.MarketplaceSource)
            || CliPluginLoadContext.HostHas(package))
            throw new DashboardMarketplaceException(409, $"already installed by the host: {package}");
        return (plugin, own, target);
    }

    /// <summary>The flat container URL of a package (<c>{feed}/{id}/{version}/{id}.{version}.nupkg</c>, lower case).</summary>
    public string PackageUrl(string id, string version)
    {
        var lid = id.ToLowerInvariant();
        var lver = version.ToLowerInvariant();
        return $"{NuGetFeed}/{Uri.EscapeDataString(lid)}/{Uri.EscapeDataString(lver)}/{Uri.EscapeDataString(lid)}.{Uri.EscapeDataString(lver)}.nupkg";
    }

    private async Task<byte[]> DownloadAsync(string id, string version, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(PackageUrl(id, version), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            throw new DashboardMarketplaceException(502, $"download failed: {id} {version}");
        }
        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new DashboardMarketplaceException(404, $"not on the NuGet feed: {id}@{version}");
            if (!response.IsSuccessStatusCode) throw new DashboardMarketplaceException(502, $"download failed: {id} {version} ({(int)response.StatusCode})");
            if (response.Content.Headers.ContentLength > MaxPackageBytes) throw new DashboardMarketplaceException(502, $"package too large: {id}");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxPackageBytes) throw new DashboardMarketplaceException(502, $"package too large: {id}");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }

    /// <summary>The lowest version on the feed in a NuGet range (stable ones unless the range's minimum is a prerelease).</summary>
    private async Task<string?> ResolveAsync(string id, string? range, CancellationToken cancellationToken)
    {
        var index = await GetJsonAsync($"{NuGetFeed}/{Uri.EscapeDataString(id.ToLowerInvariant())}/index.json", cancellationToken);
        var minimum = NuGetPackage.RangeMinimum(range);
        var prerelease = minimum?.Contains('-') == true;
        var versions = (index?["versions"] as JsonArray)?.Select(v => v is JsonValue j && j.GetValueKind() == JsonValueKind.String ? j.GetValue<string>() : null)
            .OfType<string>().Where(v => NuGetPackage.IsVersion(v) && (prerelease || !v.Contains('-')) && NuGetPackage.SatisfiesNuGetRange(v, range))
            .OrderBy(v => v, Comparer<string>.Create(NuGetPackage.CompareVersions)).ToList();
        return versions?.FirstOrDefault() ?? minimum;
    }

    /// <summary>Packages the server never downloads: the framework's own.</summary>
    private static bool IsFramework(string id) =>
        id.Equals("NETStandard.Library", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("Microsoft.NETCore.", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("runtime.", StringComparison.OrdinalIgnoreCase)
        || id.Equals("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Downloads the package, checks it against the marketplace's sha512 and itself, extracts its assemblies and those of
    /// the dependencies the server does not have, then moves them in place. Throws without changing anything when a check fails.
    /// </summary>
    private async Task<CliServerPluginRecord> InstallPackageAsync(JsonObject plugin, JsonObject own, JsonObject target, DashboardMarketplaceActor actor,
        CancellationToken cancellationToken)
    {
        var package = Str(own, "package")!;
        var version = Str(target, "version")!;
        var integrity = Str(target, "integrity");
        if (integrity == null || !Sha512().IsMatch(integrity))
            throw new DashboardMarketplaceException(502, "the marketplace record has no sha512 integrity");
        var bytes = await DownloadAsync(package, version, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(Sha512Integrity(bytes)), System.Text.Encoding.ASCII.GetBytes(integrity)))
            throw new DashboardMarketplaceException(502, $"integrity mismatch: {package}@{version}");

        var finalDir = _plugins.VersionDir(package, version);
        var staging = $"{finalDir}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp";
        Directory.CreateDirectory(staging);
        try
        {
            var server = ServerVersion;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<NuGetDependency>();
            using (var zip = OpenPackage(bytes, package))
            {
                NuGetPackage.Nuspec nuspec;
                try
                {
                    nuspec = NuGetPackage.ReadNuspec(zip);
                }
                catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
                {
                    throw new DashboardMarketplaceException(502, $"invalid package: {package} ({ex.Message})");
                }
                if (!string.Equals(nuspec.Id, package, StringComparison.OrdinalIgnoreCase) || NuGetPackage.CompareVersions(nuspec.Version, version) != 0)
                    throw new DashboardMarketplaceException(502, $"the package is {nuspec.Id}@{nuspec.Version}, not {package}@{version}");
                var folder = NuGetPackage.LibFolder(zip)
                    ?? throw new DashboardMarketplaceException(400, $"{package}@{version} has no assemblies for .NET {NuGetPackage.RuntimeVersion}");
                var main = zip.GetEntry($"{folder}{package}.dll")
                    ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals($"{folder}{package}.dll", StringComparison.OrdinalIgnoreCase))
                    ?? throw new DashboardMarketplaceException(400, $"{package}@{version} has no {package}.dll in {folder}");
                CliPluginAssemblyInfo info;
                try
                {
                    using var stream = new MemoryStream();
                    await using (var entry = main.Open()) await entry.CopyToAsync(stream, cancellationToken);
                    stream.Position = 0;
                    info = NuGetPackage.ReadPluginAssembly(stream);
                }
                catch (BadImageFormatException)
                {
                    throw new DashboardMarketplaceException(502, $"invalid package: {package}.dll is not a .NET assembly");
                }
                if (!info.AutoInstall)
                    throw new DashboardMarketplaceException(400, $"needs setup in code{(info.AutoInstallReason != null ? $": {info.AutoInstallReason}" : string.Empty)}");
                if (!info.HasEntry)
                    throw new DashboardMarketplaceException(400, $"{package} declares no [assembly: CliPlugin(typeof(...))] entry");
                foreach (var dependency in nuspec.Dependencies)
                {
                    if (server != null && ServerCorePackages.Contains(dependency.Id, StringComparer.OrdinalIgnoreCase)
                        && !NuGetPackage.SatisfiesNuGetRange(server, dependency.Range))
                        throw new DashboardMarketplaceException(400, $"incompatible: {package}@{version} needs server {dependency.Range} (this server has {server})");
                    queue.Enqueue(dependency);
                }
                Extract(zip, folder, staging);
                seen.Add(package);
            }

            // The dependencies the server does not have, from the same feed: the version the marketplace pins with its
            // sha512 (checked), else, for a dependency the host trusts, the lowest version of the range (as NuGet resolves)
            var pinned = PinnedDependencies(target);
            while (queue.Count > 0)
            {
                var dependency = queue.Dequeue();
                if (!seen.Add(dependency.Id) || IsFramework(dependency.Id) || CliPluginLoadContext.HostHas(dependency.Id)) continue;
                if (seen.Count > MaxPackages) throw new DashboardMarketplaceException(502, $"too many dependencies: {package}");
                if (!CliServerPlugins.IsValidPackage(dependency.Id)) throw new DashboardMarketplaceException(502, $"invalid dependency: {dependency.Id}");
                string resolved;
                string? expected = null;
                if (pinned.TryGetValue(dependency.Id, out var pin))
                {
                    if (!NuGetPackage.SatisfiesNuGetRange(pin.Version, dependency.Range))
                        throw new DashboardMarketplaceException(502, $"the marketplace pins {dependency.Id} {pin.Version}, outside {dependency.Range}");
                    resolved = pin.Version;
                    expected = pin.Integrity;
                }
                else if (Trusted(dependency.Id))
                {
                    resolved = await ResolveAsync(dependency.Id, dependency.Range, cancellationToken)
                        ?? throw new DashboardMarketplaceException(502, $"dependency not found: {dependency.Id} {dependency.Range}");
                }
                else
                {
                    throw new DashboardMarketplaceException(400,
                        $"untrusted dependency: {dependency.Id} (the marketplace gives no sha512 for it; a host can allow it with TrustedDependencies)");
                }
                var depBytes = await DownloadAsync(dependency.Id, resolved, cancellationToken);
                if (expected != null && !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(Sha512Integrity(depBytes)),
                        System.Text.Encoding.ASCII.GetBytes(expected)))
                    throw new DashboardMarketplaceException(502, $"integrity mismatch: {dependency.Id}@{resolved}");
                using var zip = OpenPackage(depBytes, dependency.Id);
                NuGetPackage.Nuspec nuspec;
                try
                {
                    nuspec = NuGetPackage.ReadNuspec(zip);
                }
                catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
                {
                    throw new DashboardMarketplaceException(502, $"invalid package: {dependency.Id} ({ex.Message})");
                }
                if (!string.Equals(nuspec.Id, dependency.Id, StringComparison.OrdinalIgnoreCase))
                    throw new DashboardMarketplaceException(502, $"the package is {nuspec.Id}, not {dependency.Id}");
                foreach (var next in nuspec.Dependencies) queue.Enqueue(next);
                if (NuGetPackage.LibFolder(zip) is { } folder) Extract(zip, folder, staging);
            }

            if (Directory.Exists(finalDir)) Directory.Delete(finalDir, recursive: true);
            Directory.Move(staging, finalDir);
        }
        catch
        {
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (IOException)
            {
                // left for the next install
            }
            throw;
        }

        var title = new[] { Str(plugin, "title"), Str(plugin, "name") }.First(t => !string.IsNullOrWhiteSpace(t))!.Trim();
        var description = Str(plugin, "description");
        return new CliServerPluginRecord
        {
            Name = Str(plugin, "name")!,
            Package = package,
            Version = version,
            Integrity = integrity,
            Title = title.Length > 60 ? title[..60] : title,
            Icon = Str(plugin, "icon"),
            Description = description != null && description.Length > 500 ? description[..500] : description,
            InstalledAt = _time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            InstalledBy = actor.UserName,
        };
    }

    private static ZipArchive OpenPackage(byte[] bytes, string id)
    {
        try
        {
            return new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new DashboardMarketplaceException(502, $"invalid package: {id} is not a .nupkg");
        }
    }

    /// <summary>
    /// Extracts the folder's files into <paramref name="dir"/>: a file already there (from the plugin or an earlier
    /// dependency) wins, and an assembly the host has is never written (the plugin uses the host's: one copy).
    /// </summary>
    private static void Extract(ZipArchive zip, string folder, string dir)
    {
        long total = 0;
        foreach (var entry in NuGetPackage.LibFiles(zip, folder))
        {
            var name = Path.GetFileName(entry.FullName);
            if (string.IsNullOrEmpty(name) || name.Contains("..", StringComparison.Ordinal)) continue;
            if (CliPluginLoadContext.HostHas(Path.GetFileNameWithoutExtension(name))) continue;
            total += entry.Length;
            if (total > 4 * MaxPackageBytes) throw new DashboardMarketplaceException(502, "package too large unpacked");
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) continue;
            entry.ExtractToFile(path);
        }
    }

    /// <summary>True when the installed list differs from what this process runs for <paramref name="name"/>.</summary>
    private bool RestartPending(string name)
    {
        var record = RecordOf(name);
        var loaded = LoadedOf(name);
        return record != null ? loaded?.Version != record.Version : loaded != null;
    }

    /// <summary>Marks (or clears, when the installed list is back to what runs) the restart for a plugin; true when one is needed.</summary>
    private bool SyncRestart(string name, string description)
    {
        var pending = RestartPending(name);
        if (_restart() is { } restart)
        {
            var id = $"plugin:{name}";
            restart.ClearRequired(id);
            if (pending) restart.MarkRequired(new CliRestartRequirementInput(id, "plugins", description));
        }
        return pending;
    }

    private async Task ChangedAsync(string action, CliServerPluginRecord record, DashboardMarketplaceActor actor, string? previousVersion)
    {
        var properties = new Dictionary<string, object?> { ["name"] = record.Name, ["package"] = record.Package, ["version"] = record.Version };
        if (previousVersion != null) properties["previousVersion"] = previousVersion;
        if (_audit() is { } audit)
        {
            try
            {
                await audit.LogAsync(new AuditEntry
                {
                    Timestamp = _time.GetUtcNow().ToUnixTimeMilliseconds(),
                    Event = $"server.plugin.{action}",
                    Username = actor.UserName,
                    UserId = actor.UserId,
                    ActorId = actor.UserId,
                    Actor = actor.UserName,
                    Target = record.Name,
                    Details = previousVersion != null ? $"{record.Package} {previousVersion} -> {record.Version}" : $"{record.Package}@{record.Version}",
                    Properties = new Dictionary<string, object?>(properties),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[qcli-admin] audit of a server plugin change failed");
            }
        }
        if (_bus() is { } bus)
        {
            try
            {
                var data = new Dictionary<string, object?> { ["action"] = action };
                foreach (var (key, value) in properties) data[key] = value;
                await bus.PublishAsync(ChangedEvent, data, CliEventAudience.WithPermission(CliRoutePolicy.AdminAccess));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[qcli-admin] publishing a server plugin change failed");
            }
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _changes.Dispose();
    }
}
