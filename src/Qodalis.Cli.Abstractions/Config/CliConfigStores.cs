using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qodalis.Cli.Abstractions.Config;

/// <summary>A section's stored values (the settings changed from their defaults) and its version (0 when nothing is stored).</summary>
public sealed class CliConfigStoredSection
{
    public CliConfigStoredSection(IReadOnlyDictionary<string, JsonNode?> values, long version)
    {
        Values = values;
        Version = version;
    }

    public IReadOnlyDictionary<string, JsonNode?> Values { get; }
    public long Version { get; }

    /// <summary>Nothing stored.</summary>
    public static CliConfigStoredSection Empty => new(new Dictionary<string, JsonNode?>(), 0);

    /// <summary>A deep copy.</summary>
    public CliConfigStoredSection Clone() =>
        new(Values.ToDictionary(p => p.Key, p => p.Value?.DeepClone()), Version);

    /// <summary>The values as a JSON object.</summary>
    public JsonObject ValuesToJson()
    {
        var json = new JsonObject();
        foreach (var (key, value) in Values) json[key] = value?.DeepClone();
        return json;
    }

    /// <summary>Values read from a JSON object (anything else: none).</summary>
    public static Dictionary<string, JsonNode?> ValuesFromJson(JsonNode? json) =>
        json is JsonObject o ? o.ToDictionary(p => p.Key, p => p.Value?.DeepClone()) : new Dictionary<string, JsonNode?>();
}

/// <summary>
/// Where writable settings are kept: the values changed from their defaults, per provider and section, with a version
/// per section that grows by one on every write. Every provider may have its own store, and one store may serve several
/// providers (its data is keyed by provider and section). Shipped: in memory (<see cref="InMemoryConfigStore"/>), a JSON
/// file or a section of one (<see cref="JsonFileConfigStore"/>), a section of the app's <c>appsettings.json</c>
/// (<see cref="AppSettingsConfigStore"/>), a database (<c>EfCoreConfigStore</c> of <c>Qodalis.Cli.Plugin.Config.EfCore</c>)
/// and Redis (<c>RedisConfigStore</c> of <c>Qodalis.Cli.Plugin.Config.Redis</c>). Secrets are stored as given: keep the
/// file, table or keys where only the server can read them.
/// </summary>
public interface ICliConfigStore
{
    /// <summary>The source of the settings it holds: <c>runtime</c> (memory), <c>file</c> or <c>database</c> (Redis too).</summary>
    string Source { get; }

    /// <summary>
    /// Where it keeps the settings, shown by <c>GET /config</c> (each stored provider's <c>store</c>, and
    /// <c>defaultStore</c>), the dashboard's Config page and <c>config list</c>: the file name, the database and table or
    /// the Redis host, never credentials. A store returning null is shown as a <c>custom</c> store
    /// (<see cref="CliConfigStoreHelpers.Describe"/>).
    /// </summary>
    CliConfigStoreDescription? Describe() => null;

    /// <summary>
    /// Completes once the store can be used (e.g. its connection is open): the server's config ready
    /// (<c>CliConfigService.ReadyAsync</c>) waits for it, besides every provider's stored values. Optional.
    /// </summary>
    Task ReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>A section's values; empty with version 0 when nothing is stored.</summary>
    Task<CliConfigStoredSection> ReadAsync(string provider, string section, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies <paramref name="changes"/> to a section atomically (null removes a key) and returns the section as
    /// stored. With <paramref name="expectedVersion"/>, throws <see cref="CliConfigConflictException"/> when the stored
    /// version differs.
    /// </summary>
    Task<CliConfigStoredSection> WriteAsync(string provider, string section, IReadOnlyDictionary<string, JsonNode?> changes,
        long? expectedVersion = null, CancellationToken cancellationToken = default);
}

/// <summary>Helpers shared by the stores.</summary>
public static class CliConfigStoreHelpers
{
    /// <summary>The storage of a provider whose changes are kept in a config store (the provider view's <c>store</c> says which).</summary>
    public static CliConfigProviderStorage StoreStorage() => new()
    {
        Kind = CliConfigStorageKinds.Store,
        Description = "Changes are saved to its config store",
    };

    /// <summary>A store's description: its own <see cref="ICliConfigStore.Describe"/>, else a <c>custom</c> store named after its class.</summary>
    public static CliConfigStoreDescription Describe(ICliConfigStore store)
    {
        var own = store.Describe();
        if (own != null)
            return new CliConfigStoreDescription { Kind = own.Kind, Title = own.Title, Detail = own.Detail ?? string.Empty, Persistent = own.Persistent };
        return new CliConfigStoreDescription
        {
            Kind = CliConfigStoreKinds.Custom,
            Title = "Custom store",
            Detail = store.GetType().Name,
            Persistent = store.Source != CliConfigSources.Runtime,
        };
    }

    /// <summary>The store as one line: <c>JSON file (qcli-config.json), kept across restarts</c>.</summary>
    public static string DescribeLine(CliConfigStoreDescription store) =>
        $"{DescribeName(store)}, {(store.Persistent ? "kept across restarts" : "changes are lost on restart")}";

    /// <summary>The store's title and detail: <c>Redis (cache:6379/0, prefix qcli:config)</c>, <c>In memory</c>.</summary>
    public static string DescribeName(CliConfigStoreDescription store) =>
        $"{store.Title}{(string.IsNullOrEmpty(store.Detail) ? "" : $" ({store.Detail})")}";

    /// <summary>A path relative to the working directory when inside it, else the file name only (no absolute paths in descriptions).</summary>
    public static string DisplayPath(string filePath)
    {
        var absolute = Path.GetFullPath(filePath);
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), absolute);
        if (relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            return relative.Replace(Path.DirectorySeparatorChar, '/');
        return Path.GetFileName(absolute);
    }

    /// <summary>Applies changes to stored values: null removes the key.</summary>
    public static Dictionary<string, JsonNode?> ApplyChanges(IReadOnlyDictionary<string, JsonNode?> values,
        IReadOnlyDictionary<string, JsonNode?> changes)
    {
        var next = values.ToDictionary(p => p.Key, p => p.Value?.DeepClone());
        foreach (var (key, value) in changes)
        {
            if (value == null) next.Remove(key);
            else next[key] = value.DeepClone();
        }
        return next;
    }

    /// <summary>The next state of a section, or <see cref="CliConfigConflictException"/> on a stale version.</summary>
    public static CliConfigStoredSection Next(CliConfigStoredSection current, IReadOnlyDictionary<string, JsonNode?> changes,
        long? expectedVersion)
    {
        if (expectedVersion != null && expectedVersion != current.Version)
            throw new CliConfigConflictException(current.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new CliConfigStoredSection(ApplyChanges(current.Values, changes), current.Version + 1);
    }
}

/// <summary>Keeps the settings in memory: changes apply at runtime and are lost on restart (source <c>runtime</c>).</summary>
public class InMemoryConfigStore : ICliConfigStore
{
    private readonly Dictionary<(string, string), CliConfigStoredSection> _data = new();
    private readonly object _lock = new();

    public string Source => CliConfigSources.Runtime;

    public virtual CliConfigStoreDescription? Describe() =>
        new() { Kind = CliConfigStoreKinds.Memory, Title = "In memory", Detail = string.Empty, Persistent = false };

    public Task<CliConfigStoredSection> ReadAsync(string provider, string section, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_data.TryGetValue((provider, section), out var s) ? s.Clone() : CliConfigStoredSection.Empty);
        }
    }

    public Task<CliConfigStoredSection> WriteAsync(string provider, string section, IReadOnlyDictionary<string, JsonNode?> changes,
        long? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var current = _data.TryGetValue((provider, section), out var s) ? s : CliConfigStoredSection.Empty;
            var next = CliConfigStoreHelpers.Next(current, changes, expectedVersion);
            _data[(provider, section)] = next;
            return Task.FromResult(next.Clone());
        }
    }
}

/// <summary>Options of <see cref="JsonFileConfigStore"/>.</summary>
public sealed class JsonFileConfigStoreOptions
{
    /// <summary>
    /// Keep the settings in this section of an existing JSON settings file (the app's own, like <c>appsettings.json</c>)
    /// instead of the whole file: <c>qcli</c>, or a nested one as <c>app.qcli</c> or <c>Qcli:Config</c>. The rest of
    /// the file is kept (re-read on every write); the section is created when missing.
    /// </summary>
    public string? Section { get; init; }
}

/// <summary>
/// Keeps the settings in a JSON file (source <c>file</c>), read once and rewritten on every change (written to a
/// temporary file, then renamed):
/// <code>{ "custom": { "custom": { "version": 2, "values": { "motd": "hello" } } } }</code>
/// The whole file by default (created with mode 0600 on Unix; only this process writes it: changes made to it while
/// the server runs are overwritten). With <see cref="JsonFileConfigStoreOptions.Section"/>, one section of the app's own
/// settings file: <c>new JsonFileConfigStore("config/app.json", new() { Section = "qcli" })</c> keeps the settings under
/// <c>"qcli"</c>, re-reads the file on every write and leaves everything else in it (keys, order, indentation, line
/// endings, the file's mode) as it was. Comments in the file are allowed but not kept by a write.
/// </summary>
public class JsonFileConfigStore : ICliConfigStore
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _lock = new();
    private Dictionary<string, Dictionary<string, CliConfigStoredSection>>? _data;

    public JsonFileConfigStore(string filePath, JsonFileConfigStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
        var section = options?.Section?.Trim() ?? string.Empty;
        SectionPath = section.Length == 0 ? [] : section.Split('.', ':');
        if (SectionPath.Any(part => part.Length == 0))
            throw new ArgumentException($"invalid config file section: {options?.Section}");
    }

    public string FilePath { get; }

    /// <summary>The section's path in the file (<c>["app", "qcli"]</c>); empty for the whole file.</summary>
    public IReadOnlyList<string> SectionPath { get; }

    public string Source => CliConfigSources.File;

    /// <summary>
    /// The file's path relative to the working directory, or its name when outside it; and the section
    /// (<c>config/app.json, section qcli</c>).
    /// </summary>
    public virtual CliConfigStoreDescription? Describe() => new()
    {
        Kind = CliConfigStoreKinds.File,
        Title = "JSON file",
        Detail = CliConfigStoreHelpers.DisplayPath(FilePath) + (SectionPath.Count > 0 ? $", section {string.Join('.', SectionPath)}" : ""),
        Persistent = true,
    };

    /// <summary>The whole file parsed (null when missing or empty) and its text.</summary>
    private (JsonNode? Root, string Text, bool Bom) ReadFile()
    {
        if (!File.Exists(FilePath)) return (null, string.Empty, false);
        var bytes = File.ReadAllBytes(FilePath);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new System.Text.UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        try
        {
            return (string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text, documentOptions: ParseOptions), text, bom);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"config file {FilePath} is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>The section's node in the parsed file (null when missing); throws when a part of the path is not an object.</summary>
    private JsonNode? SectionOf(JsonNode? root)
    {
        var node = root;
        for (var i = 0; i < SectionPath.Count; i++)
        {
            if (node == null) return null;
            if (node is not JsonObject o) throw NotAnObject(i);
            node = o[SectionPath[i]];
        }
        if (node != null && node is not JsonObject) throw NotAnObject(SectionPath.Count);
        return node;
    }

    private InvalidOperationException NotAnObject(int depth) =>
        new($"config file {FilePath}: {(depth == 0 ? "the file" : string.Join('.', SectionPath.Take(depth)))} is not an object");

    private Dictionary<string, Dictionary<string, CliConfigStoredSection>> Load()
    {
        if (_data != null) return _data;
        var root = ReadFile().Root;
        _data = Parse(SectionPath.Count > 0 ? SectionOf(root) : root);
        return _data;
    }

    /// <summary>Stored data from parsed JSON: malformed sections and versions are skipped.</summary>
    private static Dictionary<string, Dictionary<string, CliConfigStoredSection>> Parse(JsonNode? parsed)
    {
        var data = new Dictionary<string, Dictionary<string, CliConfigStoredSection>>();
        if (parsed is not JsonObject providers) return data;
        foreach (var (provider, sectionsNode) in providers)
        {
            if (sectionsNode is not JsonObject sections) continue;
            foreach (var (section, stored) in sections)
            {
                if (stored is not JsonObject s) continue;
                var values = CliConfigStoredSection.ValuesFromJson(s["values"]);
                var version = CliConfigValues.AsNumber(s["version"]) is { } v && v >= 0 && v % 1 == 0 ? (long)v : 0;
                if (!data.TryGetValue(provider, out var map)) data[provider] = map = new();
                map[section] = new CliConfigStoredSection(values, version);
            }
        }
        return data;
    }

    private static JsonObject ToJson(Dictionary<string, Dictionary<string, CliConfigStoredSection>> data)
    {
        var json = new JsonObject();
        foreach (var (provider, sections) in data)
        {
            var map = new JsonObject();
            foreach (var (section, stored) in sections)
                map[section] = new JsonObject { ["version"] = stored.Version, ["values"] = stored.ValuesToJson() };
            json[provider] = map;
        }
        return json;
    }

    private void Save(Dictionary<string, Dictionary<string, CliConfigStoredSection>> data)
    {
        var full = Path.GetFullPath(FilePath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        string text;
        var bom = false;
        if (SectionPath.Count > 0)
        {
            // The rest of the file as it is now: re-read it, replace the section only
            var current = ReadFile();
            bom = current.Bom;
            var root = current.Root as JsonObject ?? new JsonObject();
            SectionOf(root);
            var node = root;
            foreach (var part in SectionPath.Take(SectionPath.Count - 1))
            {
                if (node[part] is not JsonObject child) node[part] = child = new JsonObject();
                node = child;
            }
            node[SectionPath[^1]] = ToJson(data);
            var indent = System.Text.RegularExpressions.Regex.Match(current.Text, @"^([ \t]+)\S", System.Text.RegularExpressions.RegexOptions.Multiline);
            var indentText = indent.Success ? indent.Groups[1].Value : "  ";
            var newLine = current.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            text = root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                IndentCharacter = indentText[0],
                IndentSize = Math.Clamp(indentText.Length, 1, 127),
                NewLine = newLine,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            if (current.Text.Length == 0 || current.Text.EndsWith('\n')) text += newLine;
            if (!OperatingSystem.IsWindows() && File.Exists(full)) mode = File.GetUnixFileMode(full);
        }
        else
        {
            text = ToJson(data).ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        }

        var temp = $"{full}.{Environment.ProcessId}.tmp";
        try
        {
            var bytes = new System.Text.UTF8Encoding(bom).GetPreamble().Concat(new System.Text.UTF8Encoding(false).GetBytes(text)).ToArray();
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllBytes(temp, bytes);
            }
            else
            {
                using (var stream = new FileStream(temp, new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                }))
                {
                    stream.Write(bytes);
                }
                File.SetUnixFileMode(temp, mode);
            }
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // nothing to remove
            }
            throw;
        }
    }

    public Task<CliConfigStoredSection> ReadAsync(string provider, string section, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var data = Load();
            return Task.FromResult(data.TryGetValue(provider, out var map) && map.TryGetValue(section, out var s)
                ? s.Clone()
                : CliConfigStoredSection.Empty);
        }
    }

    public Task<CliConfigStoredSection> WriteAsync(string provider, string section, IReadOnlyDictionary<string, JsonNode?> changes,
        long? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var data = Load();
            if (!data.TryGetValue(provider, out var map)) map = new();
            var current = map.TryGetValue(section, out var s) ? s : CliConfigStoredSection.Empty;
            var next = CliConfigStoreHelpers.Next(current, changes, expectedVersion);

            // Saved from a copy: when writing fails, what is in memory stays what is on disk
            var copy = data.ToDictionary(p => p.Key, p => new Dictionary<string, CliConfigStoredSection>(p.Value));
            if (!copy.TryGetValue(provider, out var copyMap)) copy[provider] = copyMap = new();
            copyMap[section] = next;
            Save(copy);
            _data = copy;
            return Task.FromResult(next.Clone());
        }
    }
}

/// <summary>Options of <see cref="AppSettingsConfigStore"/>.</summary>
public sealed class AppSettingsConfigStoreOptions
{
    /// <summary>The settings file; default <c>appsettings.json</c> (or <c>appsettings.{Environment}.json</c>) in <see cref="ContentRoot"/>.</summary>
    public string? FilePath { get; set; }

    /// <summary>Keep the settings in <c>appsettings.{Environment}.json</c> (e.g. <c>Development</c>) instead of <c>appsettings.json</c>.</summary>
    public string? Environment { get; set; }

    /// <summary>Where the settings file is (default the working directory; the host's content root with <c>cli.SetAppSettingsConfigStore</c>).</summary>
    public string? ContentRoot { get; set; }

    /// <summary>The section the settings are kept in (default <c>Qcli:Config</c>): <c>Qcli:Config:&lt;provider&gt;:&lt;section&gt;</c>.</summary>
    public string Section { get; set; } = AppSettingsConfigStore.DefaultSection;
}

/// <summary>
/// Keeps the settings in a section of the app's <c>appsettings.json</c> (source <c>file</c>; default section
/// <c>Qcli:Config</c>), next to the rest of its configuration, which is left as it is: the file is re-read on every
/// write, only the section is replaced and the file is written atomically. Each provider section is kept with its
/// version (<c>"Qcli": { "Config": { "jobs": { "history": { "version": 1, "values": { ... } } } } }</c>), so a host whose
/// <c>IConfiguration</c> reads the file with <c>reloadOnChange</c> (the default of ASP.NET Core) sees every change at
/// <c>Qcli:Config:&lt;provider&gt;:&lt;section&gt;:values:&lt;key&gt;</c>.
/// <code>
/// cli.SetAppSettingsConfigStore("billing");                                      // one provider, in appsettings.json
/// cli.SetConfigStore(new AppSettingsConfigStore(new() { Environment = "Development" }));  // every provider
/// </code>
/// </summary>
public class AppSettingsConfigStore : JsonFileConfigStore
{
    /// <summary>The section the settings are kept in unless another is given.</summary>
    public const string DefaultSection = "Qcli:Config";

    public AppSettingsConfigStore(AppSettingsConfigStoreOptions? options = null)
        : this(ResolvePath(options ?? new()), (options ?? new()).Section)
    {
    }

    public AppSettingsConfigStore(string filePath, string section = DefaultSection)
        : base(filePath, new JsonFileConfigStoreOptions { Section = string.IsNullOrWhiteSpace(section) ? DefaultSection : section })
    {
        Section = string.IsNullOrWhiteSpace(section) ? DefaultSection : section.Trim();
    }

    /// <summary>The section the settings are kept in, as given (<c>Qcli:Config</c>).</summary>
    public string Section { get; }

    /// <summary>The settings file (<c>appsettings.json</c>, relative to the working directory) and the section: <c>appsettings.json (section Qcli:Config)</c>.</summary>
    public override CliConfigStoreDescription? Describe() => new()
    {
        Kind = CliConfigStoreKinds.File,
        Title = CliConfigStoreHelpers.DisplayPath(FilePath),
        Detail = $"section {Section}",
        Persistent = true,
    };

    private static string ResolvePath(AppSettingsConfigStoreOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.FilePath)) return options.FilePath;
        var name = string.IsNullOrWhiteSpace(options.Environment) ? "appsettings.json" : $"appsettings.{options.Environment.Trim()}.json";
        return string.IsNullOrWhiteSpace(options.ContentRoot) ? name : Path.Combine(options.ContentRoot, name);
    }
}
