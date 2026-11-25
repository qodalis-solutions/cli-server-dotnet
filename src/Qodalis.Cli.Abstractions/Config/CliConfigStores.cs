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
/// per section that grows by one on every write. Shipped: in memory (<see cref="InMemoryConfigStore"/>), a JSON file
/// (<see cref="JsonFileConfigStore"/>) and a database (<c>EfCoreConfigStore</c> of <c>Qodalis.Cli.Plugin.Config.EfCore</c>).
/// Secrets are stored as given: keep the file or table where only the server can read it.
/// </summary>
public interface ICliConfigStore
{
    /// <summary>The source of the settings it holds: <c>runtime</c> (memory), <c>file</c> or <c>database</c>.</summary>
    string Source { get; }

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

/// <summary>
/// Keeps the settings in a JSON file (source <c>file</c>), read once and rewritten on every change (written to a
/// temporary file, then renamed; mode 0600 on Unix):
/// <code>{ "custom": { "custom": { "version": 2, "values": { "motd": "hello" } } } }</code>
/// Only this process writes the file: changes made to it while the server runs are overwritten.
/// </summary>
public class JsonFileConfigStore : ICliConfigStore
{
    private readonly object _lock = new();
    private Dictionary<string, Dictionary<string, CliConfigStoredSection>>? _data;

    public JsonFileConfigStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public string FilePath { get; }

    public string Source => CliConfigSources.File;

    private Dictionary<string, Dictionary<string, CliConfigStoredSection>> Load()
    {
        if (_data != null) return _data;
        JsonNode? parsed = null;
        if (File.Exists(FilePath))
        {
            var text = File.ReadAllText(FilePath);
            try
            {
                parsed = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"config file {FilePath} is not valid JSON: {ex.Message}", ex);
            }
        }

        var data = new Dictionary<string, Dictionary<string, CliConfigStoredSection>>();
        if (parsed is JsonObject providers)
        {
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
        }
        _data = data;
        return data;
    }

    private void Save(Dictionary<string, Dictionary<string, CliConfigStoredSection>> data)
    {
        var json = new JsonObject();
        foreach (var (provider, sections) in data)
        {
            var map = new JsonObject();
            foreach (var (section, stored) in sections)
                map[section] = new JsonObject { ["version"] = stored.Version, ["values"] = stored.ValuesToJson() };
            json[provider] = map;
        }

        var full = Path.GetFullPath(FilePath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = $"{full}.{Environment.ProcessId}.tmp";
        var text = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(temp, text);
        }
        else
        {
            using (var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(text);
            }
        }
        File.Move(temp, full, overwrite: true);
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
