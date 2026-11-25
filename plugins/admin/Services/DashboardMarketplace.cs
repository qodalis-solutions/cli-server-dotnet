using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Models;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>
/// The admin plugin's dashboard plugins from the marketplace (<see cref="AdminConfig.Marketplace"/>): on by default.
/// </summary>
public sealed class DashboardMarketplaceOptions
{
    /// <summary>The marketplace's default URL.</summary>
    public const string DefaultUrl = "https://plugins.qodalis.com";

    /// <summary>False turns the marketplace off: no routes, no <c>admin dashboard</c> command, installed plugins not loaded.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The marketplace (default <c>https://plugins.qodalis.com</c>).</summary>
    public string Url { get; set; } = DefaultUrl;

    /// <summary>
    /// Who may install: <c>verified</c> (default): listed versions of verified publishers only; <c>listed</c>: any
    /// listed version; <c>off</c>: browse only.
    /// </summary>
    public string Install { get; set; } = "verified";

    /// <summary>Where installed plugins are kept (default <c>&lt;content root&gt;/qcli-data/dashboard-plugins</c>).</summary>
    public string? Dir { get; set; }

    /// <summary>The HTTP handler for the marketplace and the CDNs (default: a <see cref="SocketsHttpHandler"/>); tests pass a fake.</summary>
    public HttpMessageHandler? HttpHandler { get; set; }

    /// <summary>The CDNs bundles are downloaded from, in order (default jsDelivr, then unpkg).</summary>
    public IList<string> Cdns { get; set; } = [.. DashboardMarketplace.DefaultCdns];

    /// <summary>Timeout of each request (default 15 seconds).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Throws <see cref="ArgumentException"/> <c>invalid marketplace url</c> or <c>invalid marketplace install policy</c>.</summary>
    public void Validate()
    {
        if (!Enabled) return;
        var url = (Url ?? string.Empty).Trim().TrimEnd('/');
        if (!Regex.IsMatch(url, @"^https?://[^/\s]+", RegexOptions.IgnoreCase))
            throw new ArgumentException($"invalid marketplace url: {Url}", nameof(Url));
        if (Install is not ("verified" or "listed" or "off"))
            throw new ArgumentException($"invalid marketplace install policy: {Install}", nameof(Install));
        if (Timeout <= TimeSpan.Zero) throw new ArgumentException("invalid marketplace timeout", nameof(Timeout));
    }
}

/// <summary>A refused or failed marketplace operation: the HTTP status and the <c>{ error }</c> text.</summary>
public sealed class DashboardMarketplaceException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Who installs or removes a plugin (recorded as <c>installedBy</c> and in the audit log).</summary>
public sealed record DashboardMarketplaceActor(string? UserId, string UserName)
{
    /// <summary>Nobody signed in (a server without sign-in).</summary>
    public static DashboardMarketplaceActor Anonymous { get; } = new(null, "anonymous");
}

/// <summary>One file of an installed plugin as <c>installed.json</c> keeps it.</summary>
public sealed class InstalledDashboardPluginFile
{
    /// <summary>The stored file: <c>extension.js</c> or <c>extension.css</c>.</summary>
    [JsonPropertyName("file")] public required string File { get; init; }

    /// <summary>The file in the npm package (<c>dist/extension.js</c>).</summary>
    [JsonPropertyName("path")] public required string Path { get; init; }

    /// <summary><c>sha384-…</c>, as the marketplace published it and the stored bytes match.</summary>
    [JsonPropertyName("integrity")] public required string Integrity { get; init; }

    [JsonPropertyName("size")] public long? Size { get; init; }
}

/// <summary>An installed plugin as <c>installed.json</c> keeps it.</summary>
public sealed class InstalledDashboardPluginRecord
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }

    /// <summary>The dashboard extension's id (its page: <c>/x/&lt;id&gt;</c>, its files: <c>/qcli/modules/&lt;id&gt;/</c>).</summary>
    [JsonPropertyName("id")] public required string Id { get; init; }

    [JsonPropertyName("title")] public required string Title { get; init; }

    [JsonPropertyName("icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; init; }

    [JsonPropertyName("files")] public required IReadOnlyList<InstalledDashboardPluginFile> Files { get; init; }

    /// <summary>ISO 8601 UTC.</summary>
    [JsonPropertyName("installedAt")] public required string InstalledAt { get; init; }

    [JsonPropertyName("installedBy")] public required string InstalledBy { get; init; }
}

/// <summary>An installed plugin as <c>GET /installed</c> lists it.</summary>
public sealed class InstalledDashboardPlugin
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }

    [JsonPropertyName("icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Icon { get; init; }

    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("installedAt")] public required string InstalledAt { get; init; }
    [JsonPropertyName("installedBy")] public required string InstalledBy { get; init; }

    /// <summary>Whether the marketplace has a newer installable version; absent when it was not asked or unreachable.</summary>
    [JsonPropertyName("updateAvailable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? UpdateAvailable { get; init; }

    internal static InstalledDashboardPlugin Of(InstalledDashboardPluginRecord r, bool? updateAvailable = null) => new()
    {
        Name = r.Name,
        Version = r.Version,
        Title = r.Title,
        Icon = r.Icon,
        Id = r.Id,
        InstalledAt = r.InstalledAt,
        InstalledBy = r.InstalledBy,
        UpdateAvailable = updateAvailable,
    };
}

/// <summary>The result of an update: the plugin, whether it changed, and the version before.</summary>
public sealed record DashboardPluginUpdate(InstalledDashboardPlugin Plugin, bool Changed, string PreviousVersion);

/// <summary>
/// Dashboard plugins from the marketplace: browses its <c>kind=dashboard</c> catalog, installs, updates and removes
/// plugins (bundles from jsDelivr then unpkg, checked against the marketplace's sha384 before anything is stored),
/// keeps them in <c>&lt;dir&gt;/&lt;name&gt;/&lt;version&gt;/</c> with <c>&lt;dir&gt;/installed.json</c> (written
/// atomically), registers each as a dashboard extension and serves its files at <c>/qcli/modules/&lt;id&gt;/</c>.
/// Changes run one at a time.
/// </summary>
public sealed partial class DashboardMarketplace : IDisposable
{
    /// <summary>The dashboard extension API this server's dashboard implements; versions needing a newer one are refused.</summary>
    public const int DashboardApiVersion = 1;

    /// <summary>The public API of the marketplace, below its URL.</summary>
    public const string MarketplaceApiPath = "/api/v1/qcli/marketplace";

    /// <summary>Published when a plugin is installed, updated or removed (audience <c>admin:access</c>).</summary>
    public const string ExtensionsChangedEvent = "dashboard:extensions-changed";

    /// <summary>The most bytes one file of a dashboard plugin may have.</summary>
    public const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>Where the plugins' files are served.</summary>
    public const string ModulesPrefix = "/qcli/modules";

    /// <summary>Below the content root.</summary>
    public static readonly string DefaultDir = System.IO.Path.Combine("qcli-data", "dashboard-plugins");

    /// <summary>jsDelivr, then unpkg.</summary>
    public static readonly IReadOnlyList<string> DefaultCdns = ["https://cdn.jsdelivr.net/npm", "https://unpkg.com"];

    private const string InstalledFile = "installed.json";

    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly CliDashboardExtensions _extensions;
    private readonly Func<IReadOnlyList<CliInstalledPlugin>> _serverPlugins;
    private readonly Func<IAuditStore?> _audit;
    private readonly Func<ICliEventBus?> _bus;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly string[] _cdns;
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly object _lock = new();
    // Installed plugins by page id, in installation order
    private readonly List<InstalledDashboardPluginRecord> _plugins = [];

    public DashboardMarketplace(
        DashboardMarketplaceOptions options,
        string contentRoot,
        CliDashboardExtensions extensions,
        Func<IReadOnlyList<CliInstalledPlugin>> serverPlugins,
        Func<IAuditStore?>? audit = null,
        Func<ICliEventBus?>? bus = null,
        ILogger? logger = null,
        TimeProvider? time = null)
    {
        options.Validate();
        Enabled = options.Enabled;
        Url = (options.Url ?? DashboardMarketplaceOptions.DefaultUrl).Trim().TrimEnd('/');
        Install = options.Install;
        Dir = System.IO.Path.GetFullPath(options.Dir ?? System.IO.Path.Combine(contentRoot, DefaultDir));
        _extensions = extensions;
        _serverPlugins = serverPlugins;
        _audit = audit ?? (() => null);
        _bus = bus ?? (() => null);
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _cdns = (options.Cdns ?? [.. DefaultCdns]).Select(c => c.TrimEnd('/')).ToArray();
        _http = new HttpClient(options.HttpHandler ?? new SocketsHttpHandler(), disposeHandler: options.HttpHandler == null)
        {
            Timeout = options.Timeout,
        };
    }

    /// <summary>False when the host turned the marketplace off.</summary>
    public bool Enabled { get; }

    public string Url { get; }

    /// <summary><c>verified</c>, <c>listed</c> or <c>off</c>.</summary>
    public string Install { get; }

    /// <summary>Where installed plugins are kept (absolute).</summary>
    public string Dir { get; }

    /// <summary><c>GET /</c>: the marketplace, the install policy and the dashboard API this server runs.</summary>
    public JsonObject Info() => new()
    {
        ["marketplaceUrl"] = Url,
        ["install"] = Install,
        ["dashboardApi"] = DashboardApiVersion,
    };

    /// <summary>True when <paramref name="id"/> is the page of an installed marketplace plugin.</summary>
    public bool HasExtension(string id)
    {
        lock (_lock) return _plugins.Any(p => p.Id == id);
    }

    // ---------------------------------------------------------------- names, versions, hashes

    [GeneratedRegex(@"^(?:@[a-z0-9\-~][a-z0-9\-._~]*/)?[a-z0-9\-~][a-z0-9\-._~]*\z")]
    private static partial Regex NpmName();

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z")]
    private static partial Regex Semver();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,39}\z")]
    private static partial Regex ExtensionId();

    [GeneratedRegex(@"^@qodalis/cli-dashboard-module-([a-z0-9][a-z0-9-]{0,39})\z")]
    private static partial Regex OfficialModule();

    [GeneratedRegex(@"^[A-Za-z0-9_-][A-Za-z0-9._-]*(?:/[A-Za-z0-9_-][A-Za-z0-9._-]*)*\z")]
    private static partial Regex FilePath();

    [GeneratedRegex(@"^sha384-[A-Za-z0-9+/]+={0,2}\z")]
    private static partial Regex Integrity();

    /// <summary>True for a valid npm package name (lower case, optionally scoped, at most 214 characters).</summary>
    public static bool IsValidName(string? name) => name != null && name.Length <= 214 && NpmName().IsMatch(name);

    /// <summary>True for a semver version (<c>1.2.3</c>, <c>1.2.3-beta.1</c>).</summary>
    public static bool IsValidVersion(string? version) => version != null && version.Length <= 64 && Semver().IsMatch(version);

    /// <summary>
    /// The directory a package is kept in: the scope's <c>/</c> as <c>+</c> (<c>@acme/tickets</c> -&gt;
    /// <c>@acme+tickets</c>), which no npm name contains, so names never clash or leave the storage directory. Throws
    /// 400 <c>invalid package name</c>.
    /// </summary>
    public static string DirName(string? name)
    {
        if (!IsValidName(name)) throw new DashboardMarketplaceException(400, "invalid package name");
        return name!.Replace('/', '+');
    }

    /// <summary>
    /// The dashboard extension id of a package: <c>@qodalis/cli-dashboard-module-&lt;id&gt;</c> -&gt; <c>&lt;id&gt;</c>,
    /// else the extension's own id, else the package's own name (scope dropped, <c>cli-dashboard-</c> and
    /// <c>module-</c>/<c>plugin-</c> prefixes dropped, other characters as <c>-</c>, at most 40). Null when nothing fits.
    /// </summary>
    public static string? PluginId(string name, string? extensionId = null)
    {
        var official = OfficialModule().Match(name);
        if (official.Success) return official.Groups[1].Value;
        if (extensionId != null && ExtensionId().IsMatch(extensionId)) return extensionId;
        var tail = name.Split('/')[^1];
        tail = Regex.Replace(tail, "^cli-dashboard-(?:module-|plugin-)?", "");
        tail = Regex.Replace(tail, "[^a-z0-9-]+", "-").TrimStart('-');
        if (tail.Length > 40) tail = tail[..40];
        tail = tail.TrimEnd('-');
        return ExtensionId().IsMatch(tail) ? tail : null;
    }

    /// <summary>Compares two semver versions (prereleases before their release); a non-semver text compares as text.</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = Semver().Match(a);
        var pb = Semver().Match(b);
        if (!pa.Success || !pb.Success) return Math.Sign(string.CompareOrdinal(a, b));
        for (var i = 1; i <= 3; i++)
        {
            var d = BigInteger(pa.Groups[i].Value).CompareTo(BigInteger(pb.Groups[i].Value));
            if (d != 0) return Math.Sign(d);
        }
        var xa = pa.Groups[4].Success ? pa.Groups[4].Value.Split('.') : null;
        var xb = pb.Groups[4].Success ? pb.Groups[4].Value.Split('.') : null;
        if (xa == null && xb == null) return 0;
        if (xa == null) return 1;
        if (xb == null) return -1;
        for (var i = 0; i < Math.Max(xa.Length, xb.Length); i++)
        {
            if (i >= xa.Length) return -1;
            if (i >= xb.Length) return 1;
            var na = xa[i].All(char.IsAsciiDigit);
            var nb = xb[i].All(char.IsAsciiDigit);
            if (na && nb)
            {
                var d = BigInteger(xa[i]).CompareTo(BigInteger(xb[i]));
                if (d != 0) return Math.Sign(d);
            }
            else if (na != nb)
            {
                return na ? -1 : 1;
            }
            else if (xa[i] != xb[i])
            {
                return Math.Sign(string.CompareOrdinal(xa[i], xb[i]));
            }
        }
        return 0;
    }

    private static System.Numerics.BigInteger BigInteger(string digits) =>
        System.Numerics.BigInteger.Parse(digits, CultureInfo.InvariantCulture);

    /// <summary><c>sha384-&lt;base64&gt;</c> of the bytes (Subresource Integrity).</summary>
    public static string Sha384Integrity(ReadOnlySpan<byte> bytes) => $"sha384-{Convert.ToBase64String(SHA384.HashData(bytes))}";

    /// <summary>
    /// The .NET server package a plugin needs: <c>serverPackages.dotnet</c> (<c>{ node, dotnet, python }</c>), or the
    /// <c>nuget</c> entry of the marketplace's list form (<c>[{ ecosystem: 'nuget', name }]</c>).
    /// </summary>
    public static string? DotnetServerPackage(JsonNode? serverPackages)
    {
        if (serverPackages is JsonArray list)
        {
            var nuget = list.OfType<JsonObject>().FirstOrDefault(p => Str(p, "ecosystem") == "nuget");
            return Str(nuget, "name") is { Length: > 0 } name ? name : null;
        }
        return serverPackages is JsonObject map && Str(map, "dotnet") is { Length: > 0 } dotnet ? dotnet : null;
    }

    // ---------------------------------------------------------------- JSON of the marketplace

    private static string? Str(JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static double? Num(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    private static int ApiOf(JsonNode? value) =>
        Num(value) is { } n && n > 0 && n == Math.Floor(n) && n <= int.MaxValue ? (int)n : 1;

    private static bool IsTrue(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    private static bool IsDashboardVersion(JsonObject v, JsonObject plugin) => (Str(v, "kind") ?? Str(plugin, "kind")) == "dashboard";

    private static IEnumerable<JsonObject> Versions(JsonObject plugin) =>
        plugin["versions"] is JsonArray versions ? versions.OfType<JsonObject>() : [];

    private static JsonObject? Newest(IEnumerable<JsonObject> versions) =>
        versions.OrderByDescending(v => Str(v, "version")!, Comparer<string>.Create(CompareVersions)).FirstOrDefault();

    /// <summary>The newest listed version, whatever API it needs.</summary>
    private static JsonObject? LatestListed(JsonObject plugin) =>
        Newest(Versions(plugin).Where(v => IsValidVersion(Str(v, "version")) && Str(v, "status") == "listed" && IsDashboardVersion(v, plugin)));

    /// <summary>The newest listed version this dashboard can run.</summary>
    private static JsonObject? LatestFitting(JsonObject plugin) =>
        Newest(Versions(plugin).Where(v => IsValidVersion(Str(v, "version")) && Str(v, "status") == "listed" && IsDashboardVersion(v, plugin)
            && ApiOf(v["dashboardApi"]) <= DashboardApiVersion));

    private sealed record PlannedFile(string File, string Path, string Integrity, long? Size);

    /// <summary>The files to store: exactly one <c>.js</c> (as <c>extension.js</c>) and at most one <c>.css</c> (as <c>extension.css</c>).</summary>
    private static List<PlannedFile> PlannedFiles(JsonNode? files)
    {
        static DashboardMarketplaceException Invalid() => new(502, "invalid marketplace record: files");
        if (files is not JsonArray list || list.Count == 0 || list.Count > 2) throw Invalid();
        var planned = new List<PlannedFile>();
        foreach (var node in list)
        {
            if (node is not JsonObject f) throw Invalid();
            var path = Str(f, "path");
            if (path == null || !FilePath().IsMatch(path) || path.Split('/').Any(s => s is "." or "..")) throw Invalid();
            var integrity = Str(f, "integrity");
            if (integrity == null || !Integrity().IsMatch(integrity)) throw Invalid();
            var file = path.EndsWith(".js", StringComparison.Ordinal) ? "extension.js" : path.EndsWith(".css", StringComparison.Ordinal) ? "extension.css" : null;
            if (file == null || planned.Any(p => p.File == file)) throw Invalid();
            long? size = null;
            if (f.ContainsKey("size"))
            {
                if (Num(f["size"]) is not { } n || n < 0) throw Invalid();
                if (n > MaxFileBytes) throw new DashboardMarketplaceException(502, $"file too large: {path}");
                size = (long)n;
            }
            planned.Add(new PlannedFile(file, path, integrity, size));
        }
        if (planned.All(p => p.File != "extension.js")) throw Invalid();
        return planned;
    }

    // ---------------------------------------------------------------- browsing

    /// <summary><c>GET /plugins</c>: the marketplace's dashboard plugins, annotated. Throws 502 when it is unreachable.</summary>
    public async Task<JsonObject> SearchAsync(string? query = null, int? page = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var qs = new List<string> { "kind=dashboard", $"dashboardApi={DashboardApiVersion}" };
        var q = query?.Trim();
        if (!string.IsNullOrEmpty(q)) qs.Add($"query={Uri.EscapeDataString(q.Length > 200 ? q[..200] : q)}");
        if (page != null) qs.Add($"page={page}");
        if (pageSize != null) qs.Add($"pageSize={pageSize}");
        var body = await GetJsonAsync($"/plugins?{string.Join('&', qs)}", cancellationToken) as JsonObject;
        if (body?["items"] is not JsonArray all) throw new DashboardMarketplaceException(502, "marketplace unreachable");
        // Only dashboard plugins, whatever an older marketplace sends
        var items = all.OfType<JsonObject>().Where(i => IsValidName(Str(i, "name")) && Str(i, "kind") == "dashboard").ToList();
        // A marketplace that ignored the kind filter counted its other plugins too: its total only holds when it sent nothing else
        var filtered = items.Count == all.Count;
        var annotated = new JsonArray();
        foreach (var item in items)
        {
            var copy = item.DeepClone().AsObject();
            Annotate(copy, item);
            annotated.Add(copy);
        }
        return new JsonObject
        {
            ["items"] = annotated,
            ["total"] = filtered && Num(body["total"]) != null ? body["total"]!.DeepClone() : items.Count,
            ["page"] = Num(body["page"]) != null ? body["page"]!.DeepClone() : page ?? 1,
            ["pageSize"] = Num(body["pageSize"]) != null ? body["pageSize"]!.DeepClone() : pageSize ?? items.Count,
        };
    }

    /// <summary><c>GET /plugins/{name}</c>: one plugin with its versions, annotated. Throws 400, 404 or 502.</summary>
    public async Task<JsonObject> DetailsAsync(string? name, CancellationToken cancellationToken = default)
    {
        var plugin = await FetchPluginAsync(name, cancellationToken);
        var copy = plugin.DeepClone().AsObject();
        var versions = new JsonArray();
        foreach (var node in (JsonArray)plugin["versions"]!)
        {
            if (node is not JsonObject v)
            {
                versions.Add(node?.DeepClone());
                continue;
            }
            var version = v.DeepClone().AsObject();
            var reason = VersionBlockedReason(plugin, v);
            version["allowed"] = reason == null;
            if (reason != null) version["blockedReason"] = reason;
            versions.Add(version);
        }
        copy["versions"] = versions;
        Annotate(copy, plugin);
        return copy;
    }

    /// <summary>Adds the annotations of <paramref name="item"/> to <paramref name="target"/>.</summary>
    private void Annotate(JsonObject target, JsonObject item)
    {
        var installed = RecordOf(Str(item, "name"));
        string? latest;
        int api;
        if (item["versions"] is JsonArray)
        {
            var fitting = LatestFitting(item);
            latest = Str(fitting, "version");
            api = fitting != null ? ApiOf(fitting["dashboardApi"]) : ApiOf(LatestListed(item)?["dashboardApi"]);
        }
        else
        {
            latest = Str(item, "version") ?? Str(item, "latest");
            api = ApiOf(item["dashboardApi"]);
        }
        var blocked = BlockedReason(item, api);
        var serverPackage = DotnetServerPackage(item["serverPackages"]);
        target["installed"] = installed == null ? null : new JsonObject { ["version"] = installed.Version };
        target["updateAvailable"] = installed != null && IsValidVersion(latest) && CompareVersions(latest!, installed.Version) > 0;
        target["allowed"] = blocked == null;
        if (blocked != null) target["blockedReason"] = blocked;
        else target.Remove("blockedReason");
        target["requires"] = serverPackage == null
            ? null
            : new JsonObject
            {
                ["serverPackage"] = serverPackage,
                ["installed"] = _serverPlugins().Any(p => string.Equals(p.Id, serverPackage, StringComparison.OrdinalIgnoreCase)),
            };
    }

    /// <summary>Why the trust policy or the API refuses a plugin: <c>install disabled</c>, <c>unverified publisher</c>, <c>needs dashboard API &lt;n&gt;</c>.</summary>
    private string? BlockedReason(JsonObject item, int api)
    {
        if (Install == "off") return "install disabled";
        if (Install == "verified" && !IsTrue(item["publisher"]?["verified"])) return "unverified publisher";
        if (api > DashboardApiVersion) return $"needs dashboard API {api}";
        return null;
    }

    private string? VersionBlockedReason(JsonObject plugin, JsonObject v)
    {
        var reason = BlockedReason(plugin, ApiOf(v["dashboardApi"]));
        if (reason != null) return reason;
        if (!IsDashboardVersion(v, plugin)) return "not a dashboard plugin";
        if (Str(v, "status") != "listed") return $"version {Str(v, "status") ?? "not listed"}";
        return null;
    }

    /// <summary>GETs a path of the marketplace's public API: its JSON, null for a 404, 502 <c>marketplace unreachable</c> otherwise.</summary>
    private async Task<JsonNode?> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Url}{MarketplaceApiPath}{path}");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) throw new DashboardMarketplaceException(502, "marketplace unreachable");
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not DashboardMarketplaceException && !cancellationToken.IsCancellationRequested)
        {
            throw new DashboardMarketplaceException(502, "marketplace unreachable");
        }
    }

    /// <summary>A plugin's details with its versions. Throws 400 for an invalid name, 404 when the marketplace does not list it.</summary>
    private async Task<JsonObject> FetchPluginAsync(string? name, CancellationToken cancellationToken)
    {
        if (!IsValidName(name)) throw new DashboardMarketplaceException(400, "invalid package name");
        if (await GetJsonAsync($"/plugins/{Uri.EscapeDataString(name!)}", cancellationToken) is not JsonObject plugin)
            throw new DashboardMarketplaceException(404, $"plugin not found: {name}");
        if (Str(plugin, "name") != name) throw new DashboardMarketplaceException(502, "marketplace unreachable");
        if (plugin["versions"] is not JsonArray) plugin["versions"] = new JsonArray();
        return plugin;
    }

    // ---------------------------------------------------------------- installed

    /// <summary>The installed plugins, in installation order.</summary>
    public IReadOnlyList<InstalledDashboardPluginRecord> Records()
    {
        lock (_lock) return _plugins.ToArray();
    }

    private InstalledDashboardPluginRecord? RecordOf(string? name)
    {
        lock (_lock) return _plugins.FirstOrDefault(p => p.Name == name);
    }

    /// <summary>
    /// <c>GET /installed</c>: the installed plugins; with <paramref name="checkUpdates"/>, <c>updateAvailable</c> from the
    /// marketplace (left out of a plugin it does not answer for).
    /// </summary>
    public async Task<IReadOnlyList<InstalledDashboardPlugin>> InstalledAsync(bool checkUpdates = false, CancellationToken cancellationToken = default)
    {
        var records = Records();
        if (!checkUpdates) return records.Select(r => InstalledDashboardPlugin.Of(r)).ToList();
        return await Task.WhenAll(records.Select(async r =>
        {
            try
            {
                var latest = Str(LatestFitting(await FetchPluginAsync(r.Name, cancellationToken)), "version");
                return InstalledDashboardPlugin.Of(r, latest != null && CompareVersions(latest, r.Version) > 0);
            }
            catch (DashboardMarketplaceException)
            {
                return InstalledDashboardPlugin.Of(r);
            }
        }));
    }

    // ---------------------------------------------------------------- changes

    /// <summary><c>POST /installed</c>: installs <paramref name="name"/> (the newest fitting version when <paramref name="version"/> is null).</summary>
    public Task<InstalledDashboardPlugin> InstallPluginAsync(string? name, string? version, DashboardMarketplaceActor actor, CancellationToken cancellationToken = default) =>
        ExclusiveAsync(async () =>
        {
            CheckNameAndVersion(name, version);
            if (RecordOf(name) is { } existing) throw new DashboardMarketplaceException(409, $"already installed: {name}@{existing.Version}");
            var (plugin, target) = await ResolveAsync(name!, version, cancellationToken);
            var id = PluginId(name!, Str(target["extension"], "id") ?? Str(plugin["extension"], "id"))
                ?? throw new DashboardMarketplaceException(400, $"no dashboard extension id for {name}");
            if (_extensions.Contains(id)) throw new DashboardMarketplaceException(409, $"dashboard extension already registered: {id}");
            var record = await StoreAsync(name!, target, id, plugin, actor, cancellationToken);
            try
            {
                Register(record);
            }
            catch (InvalidOperationException ex)
            {
                DeleteDirectory(VersionDir(name!, record.Version));
                throw new DashboardMarketplaceException(409, ex.Message);
            }
            Save();
            await ChangedAsync("install", record, actor, null);
            return InstalledDashboardPlugin.Of(record);
        });

    /// <summary>
    /// <c>PUT /installed/{name}</c>: updates to <paramref name="version"/> (default: the newest fitting one); unchanged
    /// when it already is that version. The page keeps its id.
    /// </summary>
    public Task<DashboardPluginUpdate> UpdatePluginAsync(string? name, string? version, DashboardMarketplaceActor actor, CancellationToken cancellationToken = default) =>
        ExclusiveAsync(async () =>
        {
            CheckNameAndVersion(name, version);
            var current = RecordOf(name) ?? throw new DashboardMarketplaceException(404, $"not installed: {name}");
            var (plugin, target) = await ResolveAsync(name!, version, cancellationToken);
            if (Str(target, "version") == current.Version)
                return new DashboardPluginUpdate(InstalledDashboardPlugin.Of(current), false, current.Version);
            // The page keeps its id, so open dashboards and links keep working
            var record = await StoreAsync(name!, target, current.Id, plugin, actor, cancellationToken);
            Unregister(current.Id);
            try
            {
                Register(record);
            }
            catch (InvalidOperationException ex)
            {
                Register(current);
                DeleteDirectory(VersionDir(name!, record.Version));
                throw new DashboardMarketplaceException(409, ex.Message);
            }
            Save();
            DeleteDirectory(VersionDir(name!, current.Version));
            await ChangedAsync("update", record, actor, current.Version);
            return new DashboardPluginUpdate(InstalledDashboardPlugin.Of(record), true, current.Version);
        });

    /// <summary><c>DELETE /installed/{name}</c>: unregisters the page and deletes its files.</summary>
    public Task<InstalledDashboardPlugin> RemovePluginAsync(string? name, DashboardMarketplaceActor actor) =>
        ExclusiveAsync(async () =>
        {
            CheckNameAndVersion(name, null);
            var current = RecordOf(name) ?? throw new DashboardMarketplaceException(404, $"not installed: {name}");
            Unregister(current.Id);
            Save();
            DeleteDirectory(System.IO.Path.Combine(Dir, DirName(name)));
            await ChangedAsync("remove", current, actor, null);
            return InstalledDashboardPlugin.Of(current);
        });

    private static void CheckNameAndVersion(string? name, string? version)
    {
        if (!IsValidName(name)) throw new DashboardMarketplaceException(400, "invalid package name");
        if (version != null && !IsValidVersion(version)) throw new DashboardMarketplaceException(400, "invalid version");
    }

    /// <summary>Runs changes one at a time.</summary>
    private async Task<T> ExclusiveAsync<T>(Func<Task<T>> change)
    {
        await _changes.WaitAsync();
        try
        {
            return await change();
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>The plugin and the version to install, after the kind, trust policy and API checks.</summary>
    private async Task<(JsonObject Plugin, JsonObject Target)> ResolveAsync(string name, string? version, CancellationToken cancellationToken)
    {
        var plugin = await FetchPluginAsync(name, cancellationToken);
        if (Str(plugin, "kind") != "dashboard") throw new DashboardMarketplaceException(400, $"not a dashboard plugin: {name}");
        if (Install == "off") throw new DashboardMarketplaceException(403, "install disabled");
        if (Install == "verified" && !IsTrue(plugin["publisher"]?["verified"])) throw new DashboardMarketplaceException(403, "publisher not verified");
        JsonObject? target;
        if (version != null)
        {
            target = Versions(plugin).FirstOrDefault(v => Str(v, "version") == version);
            if (target == null || Str(target, "status") != "listed") throw new DashboardMarketplaceException(404, $"version not found: {name}@{version}");
            if (!IsDashboardVersion(target, plugin)) throw new DashboardMarketplaceException(400, $"not a dashboard plugin: {name}@{version}");
            var api = ApiOf(target["dashboardApi"]);
            if (api > DashboardApiVersion) throw new DashboardMarketplaceException(400, $"needs dashboard API {api}");
        }
        else
        {
            target = LatestFitting(plugin);
            if (target == null)
            {
                if (LatestListed(plugin) is { } newest) throw new DashboardMarketplaceException(400, $"needs dashboard API {ApiOf(newest["dashboardApi"])}");
                throw new DashboardMarketplaceException(404, $"no installable version: {name}");
            }
        }
        target = target.DeepClone().AsObject();
        if (target["files"] is not JsonArray)
        {
            // An older details view without files: the version record has them
            var targetVersion = Str(target, "version")!;
            if (await GetJsonAsync($"/plugins/{Uri.EscapeDataString(name)}/versions/{Uri.EscapeDataString(targetVersion)}", cancellationToken) is not JsonObject record)
                throw new DashboardMarketplaceException(404, $"version not found: {name}@{targetVersion}");
            target["files"] = record["files"]?.DeepClone();
        }
        return (plugin, target);
    }

    /// <summary>Downloads and verifies every file, then stores them in <c>&lt;dir&gt;/&lt;name&gt;/&lt;version&gt;/</c> (staged, then moved into place).</summary>
    private async Task<InstalledDashboardPluginRecord> StoreAsync(string name, JsonObject target, string id, JsonObject plugin,
        DashboardMarketplaceActor actor, CancellationToken cancellationToken)
    {
        var version = Str(target, "version")!;
        var planned = PlannedFiles(target["files"]);
        var downloaded = new List<(InstalledDashboardPluginFile File, byte[] Bytes)>();
        foreach (var f in planned)
        {
            var bytes = await DownloadAsync(name, version, f.Path, f.Integrity, cancellationToken);
            downloaded.Add((new InstalledDashboardPluginFile { File = f.File, Path = f.Path, Integrity = f.Integrity, Size = bytes.Length }, bytes));
        }
        // Verified: now write
        var final = VersionDir(name, version);
        Directory.CreateDirectory(Dir);
        var staging = System.IO.Path.Combine(Dir, $".staging-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}");
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var (file, bytes) in downloaded)
                await File.WriteAllBytesAsync(System.IO.Path.Combine(staging, file.File), bytes, CancellationToken.None);
            Directory.CreateDirectory(System.IO.Path.Combine(Dir, DirName(name)));
            DeleteDirectory(final);
            Directory.Move(staging, final);
        }
        catch
        {
            DeleteDirectory(staging);
            throw;
        }
        var title = new[] { Str(plugin, "title"), Str(plugin, "shortName"), name }.First(t => !string.IsNullOrWhiteSpace(t))!.Trim();
        return new InstalledDashboardPluginRecord
        {
            Name = name,
            Version = version,
            Id = id,
            Title = title.Length > 40 ? title[..40] : title,
            Icon = Str(plugin, "icon"),
            Files = downloaded.Select(d => d.File).ToList(),
            InstalledAt = Now(),
            InstalledBy = actor.UserName,
        };
    }

    private string Now() => _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>One file from the first CDN whose bytes match <paramref name="integrity"/>. Throws 502 <c>integrity mismatch</c> or <c>download failed</c>.</summary>
    private async Task<byte[]> DownloadAsync(string name, string version, string path, string integrity, CancellationToken cancellationToken)
    {
        var encodedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var mismatch = false;
        foreach (var cdn in _cdns)
        {
            byte[] bytes;
            try
            {
                using var response = await _http.GetAsync($"{cdn}/{name}@{Uri.EscapeDataString(version)}/{encodedPath}",
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;
                if (response.Content.Headers.ContentLength > MaxFileBytes) continue;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > MaxFileBytes) break;
                }
                if (buffer.Length > MaxFileBytes) continue;
                bytes = buffer.ToArray();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }
            if (Sha384Integrity(bytes) == integrity) return bytes;
            mismatch = true;
        }
        throw new DashboardMarketplaceException(502, mismatch ? $"integrity mismatch: {path}" : $"download failed: {path}");
    }

    private string VersionDir(string name, string version)
    {
        var dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Dir, DirName(name), version));
        // Valid names and versions never leave the directory; checked all the same
        if (!dir.StartsWith(Dir + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) || !IsValidVersion(version))
            throw new DashboardMarketplaceException(400, "invalid version");
        return dir;
    }

    private static void DeleteDirectory(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>Adds the plugin's page (served from this server). Throws <see cref="InvalidOperationException"/> when the id is taken.</summary>
    private void Register(InstalledDashboardPluginRecord record)
    {
        var dir = VersionDir(record.Name, record.Version);
        string? Hash(string file)
        {
            var path = System.IO.Path.Combine(dir, file);
            return File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))[..12] : null;
        }

        var prefix = $"{ModulesPrefix}/{record.Id}";
        var css = record.Files.Any(f => f.File == "extension.css") ? Hash("extension.css") : null;
        CliDashboardExtension Page(string? icon) => new()
        {
            Id = record.Id,
            Title = record.Title,
            Icon = icon,
            Version = record.Version,
            Entry = $"{prefix}/extension.js?v={Hash("extension.js")}",
            Styles = css == null ? null : [$"{prefix}/extension.css?v={css}"],
        };

        try
        {
            _extensions.Add(Page(record.Icon));
        }
        catch (ArgumentException) when (record.Icon != null)
        {
            // An icon the dashboard cannot show is left out
            _extensions.Add(Page(null));
        }
        lock (_lock) _plugins.Add(record);
    }

    private void Unregister(string id)
    {
        _extensions.Remove(id);
        lock (_lock) _plugins.RemoveAll(p => p.Id == id);
    }

    /// <summary>Writes <c>installed.json</c> atomically (a temporary file moved over it).</summary>
    private void Save()
    {
        Directory.CreateDirectory(Dir);
        var file = System.IO.Path.Combine(Dir, InstalledFile);
        var temp = $"{file}.{Environment.ProcessId}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp";
        var body = JsonSerializer.Serialize(new { plugins = Records() }, FileJson) + "\n";
        try
        {
            File.WriteAllText(temp, body, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, file, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    private async Task ChangedAsync(string action, InstalledDashboardPluginRecord record, DashboardMarketplaceActor actor, string? previousVersion)
    {
        var properties = new Dictionary<string, object?> { ["name"] = record.Name, ["version"] = record.Version, ["id"] = record.Id };
        if (previousVersion != null) properties["previousVersion"] = previousVersion;
        if (_audit() is { } audit)
        {
            try
            {
                await audit.LogAsync(new AuditEntry
                {
                    Timestamp = _time.GetUtcNow().ToUnixTimeMilliseconds(),
                    Event = $"dashboard.plugin.{action}",
                    Username = actor.UserName,
                    UserId = actor.UserId,
                    ActorId = actor.UserId,
                    Actor = actor.UserName,
                    Target = record.Name,
                    Details = previousVersion != null ? $"{record.Name} {previousVersion} -> {record.Version}" : $"{record.Name}@{record.Version}",
                    Properties = new Dictionary<string, object?>(properties),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[qcli-admin] audit of a dashboard plugin change failed");
            }
        }
        if (_bus() is { } bus)
        {
            var data = new Dictionary<string, object?> { ["action"] = action };
            foreach (var (key, value) in properties) data[key] = value;
            try
            {
                await bus.PublishAsync(ExtensionsChangedEvent, data, CliEventAudience.WithPermission(CliRoutePolicy.AdminAccess));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[qcli-admin] publishing a dashboard plugin change failed");
            }
        }
    }

    // ---------------------------------------------------------------- startup and serving

    /// <summary>
    /// Registers the plugins <c>installed.json</c> lists (at startup). A plugin whose files are missing or changed on
    /// disk, or whose id is taken, is skipped with a warning; a broken <c>installed.json</c> loads nothing.
    /// </summary>
    public void Load()
    {
        if (!Enabled) return;
        var file = System.IO.Path.Combine(Dir, InstalledFile);
        if (!File.Exists(file)) return;
        JsonArray? records;
        try
        {
            records = JsonNode.Parse(File.ReadAllText(file))?["plugins"] as JsonArray;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            _logger.LogWarning("[qcli-admin] {File} is not readable JSON, no dashboard plugins loaded: {Error}", file, ex.Message);
            return;
        }
        if (records == null) return;
        foreach (var raw in records)
        {
            var name = Str(raw, "name");
            var version = Str(raw, "version");
            try
            {
                var id = Str(raw, "id");
                if (!IsValidName(name) || !IsValidVersion(version) || id == null || !ExtensionId().IsMatch(id)) throw new InvalidDataException("invalid record");
                if (RecordOf(name) != null) throw new InvalidDataException("listed twice");
                var rawFiles = raw!["files"] as JsonArray;
                var files = PlannedFiles(rawFiles == null ? null : new JsonArray(rawFiles.Select(f => (JsonNode?)new JsonObject
                {
                    ["path"] = f?["path"]?.DeepClone(),
                    ["integrity"] = f?["integrity"]?.DeepClone(),
                }).ToArray()));
                var dir = VersionDir(name!, version!);
                foreach (var f in files)
                {
                    var path = System.IO.Path.Combine(dir, f.File);
                    if (!File.Exists(path) || Sha384Integrity(File.ReadAllBytes(path)) != f.Integrity)
                        throw new InvalidDataException($"{f.File} is missing or changed on disk");
                }
                if (_extensions.Contains(id)) throw new InvalidDataException($"dashboard extension already registered: {id}");
                var title = Str(raw, "title");
                Register(new InstalledDashboardPluginRecord
                {
                    Name = name!,
                    Version = version!,
                    Id = id,
                    Title = Cut(string.IsNullOrWhiteSpace(title) ? name! : title),
                    Icon = Str(raw, "icon"),
                    Files = files.Select((f, i) => new InstalledDashboardPluginFile
                    {
                        File = f.File,
                        Path = f.Path,
                        Integrity = f.Integrity,
                        Size = Num(rawFiles![i]?["size"]) is { } size ? (long)size : null,
                    }).ToList(),
                    InstalledAt = Str(raw, "installedAt") ?? string.Empty,
                    InstalledBy = Str(raw, "installedBy") ?? string.Empty,
                });
            }
            catch (Exception ex) when (ex is InvalidDataException or DashboardMarketplaceException or InvalidOperationException or ArgumentException or IOException)
            {
                _logger.LogWarning("[qcli-admin] dashboard plugin {Name}@{Version} not loaded: {Error}", name, version, ex.Message);
            }
        }
    }

    private static string Cut(string text) => text.Length > 40 ? text[..40] : text;

    /// <summary>
    /// Serves the installed plugins' files below <c>/qcli/modules</c> (<c>/&lt;id&gt;/extension.js</c>,
    /// <c>/&lt;id&gt;/extension.css</c>) with an ETag, to anyone; false for anything else.
    /// </summary>
    public async Task<bool> TryServeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) return false;
        if (!request.Path.StartsWithSegments(ModulesPrefix, StringComparison.Ordinal, out var rest)) return false;
        var parts = (rest.Value ?? string.Empty).Split('/');
        if (parts.Length != 3 || parts[0].Length != 0 || parts[2] is not ("extension.js" or "extension.css")) return false;
        InstalledDashboardPluginRecord? record;
        lock (_lock) record = _plugins.FirstOrDefault(p => p.Id == parts[1]);
        var file = record?.Files.FirstOrDefault(f => f.File == parts[2]);
        if (record == null || file == null) return false;
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(System.IO.Path.Combine(VersionDir(record.Name, record.Version), file.File), context.RequestAborted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        var response = context.Response;
        var etag = $"\"{file.Integrity}\"";
        response.Headers.CacheControl = "no-cache";
        response.Headers.ETag = etag;
        if (request.Headers.IfNoneMatch.Any(v => v != null && v.Split(',').Select(t => t.Trim()).Contains(etag)))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return true;
        }
        response.ContentType = file.File == "extension.js" ? "text/javascript; charset=utf-8" : "text/css; charset=utf-8";
        response.ContentLength = bytes.Length;
        if (HttpMethods.IsGet(request.Method)) await response.Body.WriteAsync(bytes, context.RequestAborted);
        return true;
    }

    public void Dispose()
    {
        _http.Dispose();
        _changes.Dispose();
    }
}
