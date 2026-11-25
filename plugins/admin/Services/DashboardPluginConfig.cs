using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Qodalis.Cli.Abstractions.Config;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>A setting of a dashboard plugin (<c>qodalis.config.entries</c> of its package.json, <c>config.entries</c> of its version record).</summary>
public sealed class DashboardPluginConfigEntry
{
    [JsonPropertyName("key")] public required string Key { get; init; }

    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>One of <see cref="DashboardPluginConfigs.Types"/> (every config entry type but <c>secret</c>).</summary>
    [JsonPropertyName("type")] public required string Type { get; init; }

    /// <summary>Null when none.</summary>
    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public JsonNode? Default { get; init; }

    [JsonPropertyName("validation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CliConfigValidation? Validation { get; init; }

    /// <summary>The error of <paramref name="value"/> for this setting (the config providers' validation), or null.</summary>
    public string? Check(JsonNode? value) => CliConfigValues.Validate(Type, Validation, Default, value);
}

/// <summary>A dashboard plugin's settings.</summary>
public sealed class DashboardPluginConfig
{
    [JsonPropertyName("entries")] public required IReadOnlyList<DashboardPluginConfigEntry> Entries { get; init; }

    public DashboardPluginConfigEntry? Entry(string key) => Entries.FirstOrDefault(e => e.Key == key);
}

/// <summary>A dashboard plugin's settings the marketplace published are not acceptable: <see cref="Exception.Message"/> says why.</summary>
public sealed class DashboardPluginConfigException(string message) : Exception(message);

/// <summary>The rules of a dashboard plugin's settings, the same as the marketplace's.</summary>
public static partial class DashboardPluginConfigs
{
    /// <summary>The config provider of the dashboard plugins' settings: one section per installed plugin with settings.</summary>
    public const string ProviderId = "dashboard-plugins";

    /// <summary>The most settings a dashboard plugin may declare.</summary>
    public const int MaxEntries = 50;

    /// <summary>The most bytes a dashboard plugin's <c>config</c> may have as JSON.</summary>
    public const int MaxBytes = 32 * 1024;

    /// <summary>The types a setting may have: every config entry type but <c>secret</c> (a dashboard plugin runs in the browser).</summary>
    public static readonly IReadOnlyList<string> Types =
    [
        CliConfigEntryTypes.String, CliConfigEntryTypes.Number, CliConfigEntryTypes.Boolean, CliConfigEntryTypes.StringList,
        CliConfigEntryTypes.Enum, CliConfigEntryTypes.Json, CliConfigEntryTypes.Duration, CliConfigEntryTypes.Url,
    ];

    /// <summary>A setting's key: a letter, then letters, digits, <c>_</c>, <c>.</c> and <c>-</c> (at most 64).</summary>
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9_.-]{0,63}\z")]
    public static partial Regex Key();

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static bool IsString(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String;

    private static string Text(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => node.ToJsonString(Compact),
    };

    private static bool ValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A setting's validation rules, checked and copied (unknown rules dropped). Throws with the reason.</summary>
    private static CliConfigValidation? ParseValidation(JsonNode? raw, string key)
    {
        if (raw == null) return null;
        if (raw is not JsonObject o) throw new DashboardPluginConfigException($"{key}: validation must be an object");
        var rules = new CliConfigValidation();
        foreach (var rule in new[] { "min", "max" })
        {
            if (!o.TryGetPropertyValue(rule, out var value)) continue;
            if (value is not JsonValue v || v.GetValueKind() != JsonValueKind.Number || !double.IsFinite(v.GetValue<double>()))
                throw new DashboardPluginConfigException($"{key}: validation.{rule} must be a number");
            if (rule == "min") rules.Min = v.GetValue<double>();
            else rules.Max = v.GetValue<double>();
        }
        if (rules.Min > rules.Max) throw new DashboardPluginConfigException($"{key}: validation.min is above validation.max");
        if (o.TryGetPropertyValue("pattern", out var pattern))
        {
            if (!IsString(pattern) || pattern!.GetValue<string>().Length > 500 || !ValidRegex(pattern.GetValue<string>()))
                throw new DashboardPluginConfigException($"{key}: validation.pattern must be a regular expression");
            rules.Pattern = pattern.GetValue<string>();
        }
        if (o.TryGetPropertyValue("options", out var options))
        {
            if (options is not JsonArray list || list.Count > 200 || list.Any(i => !IsString(i)))
                throw new DashboardPluginConfigException($"{key}: validation.options must be a list of strings");
            rules.Options = list.Select(i => i!.GetValue<string>()).ToList();
        }
        if (o.TryGetPropertyValue("required", out var required))
        {
            if (required is not JsonValue r || r.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                throw new DashboardPluginConfigException($"{key}: validation.required must be true or false");
            rules.Required = r.GetValue<bool>();
        }
        return rules.Min == null && rules.Max == null && rules.Pattern == null && rules.Options == null && rules.Required == null ? null : rules;
    }

    /// <summary>
    /// A dashboard plugin's <c>config</c> as the marketplace publishes it (<c>{ entries: [...] }</c>), checked as strictly
    /// as the marketplace does: at most 50 entries with unique keys (<c>^[a-zA-Z][a-zA-Z0-9_.-]{0,63}$</c>), the types of
    /// the config providers but <c>secret</c> (a dashboard plugin runs in the browser: it cannot keep one), an
    /// <c>enum</c> with its options, defaults that pass their own validation, at most 32 KB of JSON. Null for no settings
    /// (null or no entries); throws <see cref="DashboardPluginConfigException"/> with the reason for a bad one. Unknown
    /// fields are dropped.
    /// </summary>
    public static DashboardPluginConfig? Parse(JsonNode? raw)
    {
        if (raw == null) return null;
        if (raw is not JsonObject o) throw new DashboardPluginConfigException("config must be an object");
        if (Encoding.UTF8.GetByteCount(raw.ToJsonString(Compact)) > MaxBytes) throw new DashboardPluginConfigException("config is larger than 32 KB");
        var rawEntries = o["entries"] ?? new JsonArray();
        if (rawEntries is not JsonArray list) throw new DashboardPluginConfigException("config.entries must be a list");
        if (list.Count > MaxEntries) throw new DashboardPluginConfigException($"config has more than {MaxEntries} entries");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<DashboardPluginConfigEntry>();
        foreach (var node in list)
        {
            if (node is not JsonObject e) throw new DashboardPluginConfigException("every config entry must be an object");
            var keyNode = e["key"];
            if (!IsString(keyNode) || !Key().IsMatch(keyNode!.GetValue<string>()))
                throw new DashboardPluginConfigException($"invalid config key: {Text(keyNode)}");
            var key = keyNode.GetValue<string>();
            if (!keys.Add(key)) throw new DashboardPluginConfigException($"duplicate config key: {key}");
            var typeNode = e["type"];
            if (IsString(typeNode) && typeNode!.GetValue<string>() == CliConfigEntryTypes.Secret)
                throw new DashboardPluginConfigException($"{key}: secret settings are not allowed (a dashboard plugin runs in the browser)");
            if (!IsString(typeNode) || !Types.Contains(typeNode!.GetValue<string>()))
                throw new DashboardPluginConfigException($"{key}: unknown type {Text(typeNode)}");
            var type = typeNode.GetValue<string>();
            foreach (var field in new[] { "label", "description" })
            {
                if (e[field] != null && !IsString(e[field])) throw new DashboardPluginConfigException($"{key}: {field} must be a string");
            }
            var validation = ParseValidation(e["validation"], key);
            if (type == CliConfigEntryTypes.Enum && validation?.Options is not { Count: > 0 })
                throw new DashboardPluginConfigException($"{key}: an enum needs validation.options");
            var entry = new DashboardPluginConfigEntry
            {
                Key = key,
                Label = IsString(e["label"]) ? e["label"]!.GetValue<string>() : null,
                Description = IsString(e["description"]) ? e["description"]!.GetValue<string>() : null,
                Type = type,
                Default = e["default"]?.DeepClone(),
                Validation = validation,
            };
            if (entry.Check(entry.Default) is { } error) throw new DashboardPluginConfigException($"{key}: the default {error}");
            entries.Add(entry);
        }
        return entries.Count > 0 ? new DashboardPluginConfig { Entries = entries } : null;
    }

    /// <summary>
    /// Values given for a plugin's settings (<c>config</c> of an install or update): per-key errors, the config
    /// providers' validation (<c>unknown setting</c>, the type and rules); empty when valid. Null keeps a setting at its default.
    /// </summary>
    public static Dictionary<string, string> ValidateValues(DashboardPluginConfig? config, IEnumerable<KeyValuePair<string, JsonNode?>> values)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (config?.Entry(key) is not { } entry) errors[key] = "unknown setting";
            else if (entry.Check(value) is { } error) errors[key] = error;
        }
        return errors;
    }
}

/// <summary>
/// <c>dashboard-plugins</c>: the settings of the dashboard plugins installed from the marketplace, one section per plugin
/// with settings (its page id, its title), the entries of the installed version with their defaults. Writable through
/// the config API, the <c>config</c> command and MCP like any provider (validation, versions, audit, <c>config:changed</c>).
/// Its store is resolved the usual way: the host's store for <c>dashboard-plugins</c>, else the server's default store.
/// </summary>
public sealed class DashboardPluginsConfigProvider : ICliConfigProvider, ICliConfigWriter, ICliConfigStoreAttachable
{
    private sealed record PluginSection(string Id, string Title, DashboardPluginConfig Config, string? Description);

    // Sections in the order the plugins were registered
    private readonly List<PluginSection> _sections = [];
    private readonly Dictionary<string, CliConfigStoredSection> _cache = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private ICliConfigStore? _attached;
    private ICliConfigStore? _fallback;

    public string Id => DashboardPluginConfigs.ProviderId;
    public string Title => "Dashboard plugins";
    public string? Description => "Settings of the dashboard plugins installed from the marketplace";
    public int Order => 85;

    /// <summary>Changes are saved to its config store.</summary>
    public CliConfigProviderStorage? Storage { get; } = CliConfigStoreHelpers.StoreStorage();

    /// <summary>The store the server attached; null before the provider is registered.</summary>
    public ICliConfigStore? ConfigStore
    {
        get
        {
            lock (_lock) return _attached;
        }
    }

    /// <inheritdoc />
    public Task AttachStoreAsync(ICliConfigStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (_lock)
        {
            _attached = store;
            _cache.Clear();
        }
        return Task.CompletedTask;
    }

    private ICliConfigStore Store
    {
        get
        {
            lock (_lock) return _attached ?? (_fallback ??= new InMemoryConfigStore());
        }
    }

    /// <summary>The kind of the store its values are kept in (<c>memory</c>, <c>file</c>, ...), as <c>config:changed</c> carries it.</summary>
    public string StoreKind => CliConfigStoreHelpers.Describe(Store).Kind;

    private async Task<CliConfigStoredSection> StoredAsync(string sectionId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(sectionId, out var cached)) return cached;
        }
        var stored = await Store.ReadAsync(Id, sectionId, cancellationToken);
        lock (_lock) _cache[sectionId] = stored;
        return stored;
    }

    private PluginSection? Section(string id)
    {
        lock (_lock) return _sections.FirstOrDefault(s => s.Id == id);
    }

    /// <summary>Adds or replaces a plugin's section.</summary>
    public void SetSection(string id, string title, DashboardPluginConfig config, string? description = null)
    {
        lock (_lock)
        {
            var section = new PluginSection(id, title, config, description);
            var index = _sections.FindIndex(s => s.Id == id);
            if (index >= 0) _sections[index] = section;
            else _sections.Add(section);
        }
    }

    /// <summary>Removes a plugin's section (its stored values stay unless <see cref="ClearAsync"/>ed).</summary>
    public void RemoveSection(string id)
    {
        lock (_lock) _sections.RemoveAll(s => s.Id == id);
    }

    public bool HasSection(string id) => Section(id) != null;

    /// <summary>A plugin's settings, as its section declares them.</summary>
    public DashboardPluginConfig? ConfigOf(string id) => Section(id)?.Config;

    /// <summary>The values stored for a section (the settings changed from their defaults), whether it exists or not.</summary>
    public async Task<Dictionary<string, JsonNode?>> StoredValuesAsync(string sectionId, CancellationToken cancellationToken = default) =>
        (await StoredAsync(sectionId, cancellationToken)).Values.ToDictionary(p => p.Key, p => p.Value?.DeepClone(), StringComparer.Ordinal);

    /// <summary>A section's effective values (stored over defaults) and its version; null for no section.</summary>
    public async Task<(JsonObject Values, string Version)?> ValuesAsync(string sectionId, CancellationToken cancellationToken = default)
    {
        if (Section(sectionId) is not { } section) return null;
        var stored = await StoredAsync(sectionId, cancellationToken);
        var values = new JsonObject();
        foreach (var entry in section.Config.Entries)
            values[entry.Key] = (stored.Values.TryGetValue(entry.Key, out var v) ? v : entry.Default)?.DeepClone();
        return (values, stored.Version.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Writes changes to a section's stored values (null removes a key) without the config service's checks; the new version.</summary>
    public async Task<string> WriteAsync(string sectionId, IReadOnlyDictionary<string, JsonNode?> changes, CancellationToken cancellationToken = default)
    {
        var stored = await Store.WriteAsync(Id, sectionId, changes, null, cancellationToken);
        lock (_lock) _cache[sectionId] = stored;
        return stored.Version.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Deletes every value stored for a section; the keys removed (none: nothing written, version null).</summary>
    public async Task<(IReadOnlyList<string> Keys, string? Version)> ClearAsync(string sectionId, CancellationToken cancellationToken = default)
    {
        var keys = (await StoredAsync(sectionId, cancellationToken)).Values.Keys.ToList();
        if (keys.Count == 0) return (keys, null);
        var version = await WriteAsync(sectionId, keys.ToDictionary(k => k, _ => (JsonNode?)null), cancellationToken);
        return (keys, version);
    }

    public async Task<IReadOnlyList<CliConfigSection>> GetSectionsAsync(CliConfigContext context, CancellationToken cancellationToken = default)
    {
        PluginSection[] sections;
        lock (_lock) sections = [.. _sections];
        var result = new List<CliConfigSection>();
        foreach (var section in sections)
        {
            var stored = await StoredAsync(section.Id, cancellationToken);
            var source = Store.Source;
            result.Add(new CliConfigSection
            {
                Id = section.Id,
                Title = section.Title,
                Description = section.Description,
                Version = stored.Version.ToString(CultureInfo.InvariantCulture),
                Entries = section.Config.Entries.Select(entry =>
                {
                    var overridden = stored.Values.TryGetValue(entry.Key, out var value);
                    return new CliConfigEntry
                    {
                        Key = entry.Key,
                        Label = entry.Label,
                        Description = entry.Description,
                        Type = entry.Type,
                        Value = (overridden ? value : entry.Default)?.DeepClone(),
                        Default = entry.Default?.DeepClone(),
                        Source = overridden ? source : CliConfigSources.Default,
                        Mutable = true,
                        Validation = entry.Validation?.Clone(),
                    };
                }).ToList(),
            });
        }
        return result;
    }

    public Task<IReadOnlyDictionary<string, string>> ValidateAsync(string sectionId, IReadOnlyDictionary<string, JsonNode?> changes,
        CliConfigContext context, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string>();
        if (Section(sectionId) is { } section)
        {
            foreach (var key in changes.Keys)
            {
                if (section.Config.Entry(key) == null) errors[key] = "unknown setting";
            }
        }
        return Task.FromResult<IReadOnlyDictionary<string, string>>(errors);
    }

    public async Task<CliConfigUpdateResult> UpdateAsync(string sectionId, IReadOnlyDictionary<string, JsonNode?> changes,
        string? expectedVersion, CliConfigContext context, CancellationToken cancellationToken = default)
    {
        if (Section(sectionId) == null) throw new ArgumentException($"unknown config section: {Id}.{sectionId}");
        long? expected = null;
        if (expectedVersion != null)
        {
            if (!long.TryParse(expectedVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                throw new CliConfigConflictException((await StoredAsync(sectionId, cancellationToken)).Version.ToString(CultureInfo.InvariantCulture));
            expected = parsed;
        }
        var stored = await Store.WriteAsync(Id, sectionId, changes, expected, cancellationToken);
        lock (_lock) _cache[sectionId] = stored;
        return new CliConfigUpdateResult
        {
            Applied = changes.Keys.ToList(),
            RequiresRestart = false,
            Version = stored.Version.ToString(CultureInfo.InvariantCulture),
        };
    }
}
