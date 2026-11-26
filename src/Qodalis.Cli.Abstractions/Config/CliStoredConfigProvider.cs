using System.Globalization;
using System.Text.Json.Nodes;

namespace Qodalis.Cli.Abstractions.Config;

/// <summary>A setting of a <see cref="CliStoredConfigProvider"/>.</summary>
public sealed class CliConfigSettingDefinition
{
    public required string Key { get; init; }
    public string? Label { get; init; }
    public string? Description { get; init; }

    /// <summary>One of <see cref="CliConfigEntryTypes"/>.</summary>
    public required string Type { get; init; }

    /// <summary>The value when nothing is stored (what the host configured); null when none.</summary>
    public JsonNode? Default { get; init; }

    /// <summary>Where <see cref="Default"/> comes from (e.g. <c>environment</c>); default <c>default</c>.</summary>
    public string? Source { get; init; }

    /// <summary>Can be changed (default true). A read-only setting shows <see cref="Value"/> (or <see cref="Default"/>).</summary>
    public bool Mutable { get; init; } = true;

    public bool RequiresRestart { get; init; }

    /// <summary>Never returned; every <c>secret</c> is sensitive.</summary>
    public bool Sensitive { get; init; }

    /// <summary>
    /// Must have a value (stored, or a default) for its owner to work: a server plugin without it is
    /// <c>needs-configuration</c> (see <see cref="CliStoredConfigProvider.Missing"/>). Unlike
    /// <see cref="CliConfigValidation.Required"/> it does not refuse clearing the value.
    /// </summary>
    public bool Required { get; init; }

    public CliConfigValidation? Validation { get; init; }
    public string? Editor { get; init; }

    /// <summary>The live value of a read-only setting (read on every listing).</summary>
    public Func<JsonNode?>? Value { get; init; }

    /// <summary>A further rule on a new value (after the type and <see cref="Validation"/>); the message, or null.</summary>
    public Func<JsonNode, string?>? Validate { get; init; }

    /// <summary>
    /// Makes the effective value take effect (the stored value, or <see cref="Default"/> after a reset): called for every
    /// stored value when the store is attached, and after every change.
    /// </summary>
    public Action<JsonNode?>? Apply { get; init; }
}

/// <summary>A section of a <see cref="CliStoredConfigProvider"/>.</summary>
public sealed class CliConfigSectionDefinition
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public List<CliConfigSettingDefinition> Settings { get; init; } = [];

    /// <summary>Keys not in <see cref="Settings"/> may be added and removed (null): a free key/value section.</summary>
    public bool AllowCustomKeys { get; init; }
}

/// <summary>Options of a <see cref="CliStoredConfigProvider"/>.</summary>
public sealed class CliStoredConfigProviderOptions
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public int Order { get; init; } = CliConfigConstants.DefaultOrder;
    public string? RequiredPermission { get; init; }
    public List<CliConfigSectionDefinition> Sections { get; init; } = [];

    /// <summary>
    /// The store this provider's changes are kept in. The host's store for this id (<c>cli.SetConfigStore(store, id)</c>,
    /// <c>ConfigureConfig(o =&gt; o.Stores[id] = store)</c>) still wins; without either the server's default store is used
    /// (in memory unless the host sets one).
    /// </summary>
    public ICliConfigStore? Store { get; init; }
}

/// <summary>A provider that keeps its settings in a config store, maybe its own (attached when the server starts).</summary>
public interface ICliConfigStoreAttachable
{
    /// <summary>Uses <paramref name="store"/> and applies the values stored there.</summary>
    Task AttachStoreAsync(ICliConfigStore store, CancellationToken cancellationToken = default);

    /// <summary>The provider's own store, used unless the host set one for its id; null for the server's default store.</summary>
    ICliConfigStore? OwnStore => null;
}

/// <summary>
/// A provider of settings described in code, whose changes are kept in an <see cref="ICliConfigStore"/>: the server
/// attaches a store when it starts (or when the provider is registered later) and the stored values are applied then.
/// Which store: the host's store for this provider's id (<c>cli.SetConfigStore(store, id)</c>), else the provider's own
/// (<see cref="CliStoredConfigProviderOptions.Store"/>, <see cref="SetStore"/>), else the server's default store
/// (<c>cli.SetConfigStore(store)</c>), else in memory. Built-in providers and plugins use it; a host can too:
/// <code>
/// cli.AddConfigProvider(new CliStoredConfigProvider(new CliStoredConfigProviderOptions
/// {
///     Id = "billing", Title = "Billing",
///     Store = new AppSettingsConfigStore(),
///     Sections = [new() { Id = "invoices", Settings = [
///         new() { Key = "dueDays", Type = CliConfigEntryTypes.Number, Default = 30,
///                 Validation = new() { Min = 1, Max = 90 }, Apply = v => billing.DueDays = (int)v! },
///         new() { Key = "apiKey", Type = CliConfigEntryTypes.Secret },
///     ] }],
/// }));
/// </code>
/// </summary>
public class CliStoredConfigProvider : ICliConfigProvider, ICliConfigWriter, ICliConfigStoreAttachable
{
    private readonly List<CliConfigSectionDefinition> _sections;
    private readonly Dictionary<string, CliConfigStoredSection> _cache = new();
    private readonly object _lock = new();
    private ICliConfigStore? _ownStore;
    private ICliConfigStore? _attached;
    private ICliConfigStore? _fallback;
    private Task _loading = Task.CompletedTask;

    public CliStoredConfigProvider(CliStoredConfigProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Id = options.Id;
        Title = options.Title;
        Description = options.Description;
        Order = options.Order;
        RequiredPermission = options.RequiredPermission;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in options.Sections)
        {
            if (!CliConfigConstants.Key.IsMatch(section.Id) || !ids.Add(section.Id))
                throw new ArgumentException($"invalid or duplicate config section: {section.Id}");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var setting in section.Settings)
            {
                if (!CliConfigConstants.Key.IsMatch(setting.Key) || !keys.Add(setting.Key))
                    throw new ArgumentException($"invalid or duplicate config key: {section.Id}.{setting.Key}");
            }
        }
        _sections = options.Sections;
        _ownStore = options.Store;
    }

    public string Id { get; }
    public string Title { get; }
    public string? Description { get; }
    public int Order { get; }
    public string? RequiredPermission { get; }

    /// <summary>Changes are saved to its config store.</summary>
    public CliConfigProviderStorage? Storage { get; } = CliConfigStoreHelpers.StoreStorage();

    /// <summary>The provider's own store (the <c>Store</c> option or <see cref="SetStore"/>); the host's store for this id still wins.</summary>
    public ICliConfigStore? OwnStore
    {
        get
        {
            lock (_lock) return _ownStore;
        }
    }

    /// <summary>The store its changes are kept in: the one the server attached, else its own; null before either.</summary>
    public ICliConfigStore? ConfigStore
    {
        get
        {
            lock (_lock) return _attached ?? _ownStore;
        }
    }

    /// <summary>
    /// Sets the provider's own store (as the <c>Store</c> option), before the provider is registered; afterwards it
    /// throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public CliStoredConfigProvider SetStore(ICliConfigStore? store)
    {
        lock (_lock)
        {
            if (_attached != null)
                throw new InvalidOperationException($"the config store of {Id} must be set before the provider is registered");
            _ownStore = store;
        }
        return this;
    }

    /// <summary>The store in use: the attached one, else its own, else in memory.</summary>
    protected ICliConfigStore Store
    {
        get
        {
            lock (_lock) return _attached ?? _ownStore ?? (_fallback ??= new InMemoryConfigStore());
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
            _loading = LoadAsync(store, cancellationToken);
            return _loading;
        }
    }

    private async Task LoadAsync(ICliConfigStore store, CancellationToken cancellationToken)
    {
        foreach (var section in _sections)
        {
            var stored = await store.ReadAsync(Id, section.Id, cancellationToken);
            lock (_lock) _cache[section.Id] = stored;
            foreach (var setting in section.Settings)
            {
                if (setting.Mutable && stored.Values.TryGetValue(setting.Key, out var value))
                    setting.Apply?.Invoke(value?.DeepClone());
            }
        }
    }

    /// <summary>Completes once the stored values are loaded and applied.</summary>
    public Task ReadyAsync()
    {
        lock (_lock) return _loading;
    }

    private async Task<CliConfigStoredSection> StoredAsync(string sectionId, CancellationToken cancellationToken)
    {
        await ReadyAsync();
        lock (_lock)
        {
            if (_cache.TryGetValue(sectionId, out var cached)) return cached;
        }
        var stored = await Store.ReadAsync(Id, sectionId, cancellationToken);
        lock (_lock) _cache[sectionId] = stored;
        return stored;
    }

    private CliConfigSectionDefinition? Section(string sectionId) => _sections.FirstOrDefault(s => s.Id == sectionId);

    /// <summary>
    /// A setting's effective value: the stored one, else its default (or the live value of a read-only setting).
    /// Secrets included: for the host's own code, never for clients. Null for an unknown key.
    /// </summary>
    public JsonNode? GetValue(string sectionId, string key)
    {
        var section = Section(sectionId);
        var setting = section?.Settings.FirstOrDefault(s => s.Key == key);
        IReadOnlyDictionary<string, JsonNode?>? stored;
        lock (_lock) stored = _cache.TryGetValue(sectionId, out var c) ? c.Values : null;
        if (setting != null)
        {
            if (!setting.Mutable) return setting.Value != null ? setting.Value() : setting.Default?.DeepClone();
            if (stored != null && stored.TryGetValue(key, out var v)) return v?.DeepClone();
            return setting.Default?.DeepClone();
        }
        if (section?.AllowCustomKeys == true && stored != null && stored.TryGetValue(key, out var custom)) return custom?.DeepClone();
        return null;
    }

    /// <summary>
    /// The required settings without a value (<c>section.key</c>; an empty string or list counts as none), once the
    /// stored values are loaded (<see cref="ReadyAsync"/>).
    /// </summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        foreach (var section in _sections)
        {
            foreach (var setting in section.Settings)
            {
                if (!setting.Required && setting.Validation?.Required != true) continue;
                if (CliConfigValues.IsEmpty(GetValue(section.Id, setting.Key))) missing.Add($"{section.Id}.{setting.Key}");
            }
        }
        return missing;
    }

    /// <summary>The sections as given (for a copy with other defaults).</summary>
    public IReadOnlyList<CliConfigSectionDefinition> Definitions => _sections;

    public virtual async Task<IReadOnlyList<CliConfigSection>> GetSectionsAsync(CliConfigContext context,
        CancellationToken cancellationToken = default)
    {
        var result = new List<CliConfigSection>();
        foreach (var section in _sections)
        {
            var stored = await StoredAsync(section.Id, cancellationToken);
            var source = Store.Source;
            var entries = new List<CliConfigEntry>();
            foreach (var setting in section.Settings)
            {
                var overridden = setting.Mutable && stored.Values.ContainsKey(setting.Key);
                var value = !setting.Mutable && setting.Value != null ? setting.Value()
                    : overridden ? stored.Values[setting.Key] : setting.Default;
                var sensitive = setting.Sensitive || setting.Type == CliConfigEntryTypes.Secret;
                entries.Add(new CliConfigEntry
                {
                    Key = setting.Key,
                    Label = setting.Label,
                    Description = setting.Description,
                    Type = setting.Type,
                    Value = sensitive ? null : value?.DeepClone(),
                    Default = sensitive ? null : setting.Default?.DeepClone(),
                    IsSet = sensitive ? !CliConfigValues.IsEmpty(value) : null,
                    Source = overridden ? source : setting.Source ?? CliConfigSources.Default,
                    Origin = overridden ? CliConfigOrigins.Setting : CliConfigOrigins.Of(setting.Source),
                    Mutable = setting.Mutable,
                    RequiresRestart = setting.RequiresRestart,
                    Sensitive = sensitive,
                    Required = setting.Required || setting.Validation?.Required == true,
                    Validation = setting.Validation?.Clone(),
                    Editor = setting.Editor,
                });
            }
            if (section.AllowCustomKeys)
            {
                var known = section.Settings.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
                foreach (var (key, value) in stored.Values)
                {
                    if (known.Contains(key)) continue;
                    entries.Add(new CliConfigEntry
                    {
                        Key = key,
                        Type = CliConfigValues.InferType(value),
                        Value = value?.DeepClone(),
                        Source = source,
                        Origin = CliConfigOrigins.Setting,
                        Mutable = true,
                    });
                }
            }
            var writable = section.AllowCustomKeys || section.Settings.Any(s => s.Mutable);
            result.Add(new CliConfigSection
            {
                Id = section.Id,
                Title = section.Title,
                Description = section.Description,
                Entries = entries,
                Version = writable ? stored.Version.ToString(CultureInfo.InvariantCulture) : null,
                AllowCustomKeys = section.AllowCustomKeys,
            });
        }
        return result;
    }

    public virtual Task<IReadOnlyDictionary<string, string>> ValidateAsync(string sectionId,
        IReadOnlyDictionary<string, JsonNode?> changes, CliConfigContext context, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string>();
        var section = Section(sectionId);
        if (section == null) return Task.FromResult<IReadOnlyDictionary<string, string>>(errors);
        foreach (var (key, value) in changes)
        {
            var setting = section.Settings.FirstOrDefault(s => s.Key == key);
            if (setting == null)
            {
                if (!section.AllowCustomKeys) errors[key] = "unknown setting";
                else if (!CliConfigConstants.Key.IsMatch(key)) errors[key] = "invalid key: use letters, digits, _, - and .";
                continue;
            }
            if (!setting.Mutable)
            {
                errors[key] = "read-only";
                continue;
            }
            if (value != null && setting.Validate?.Invoke(value) is { } error) errors[key] = error;
        }
        return Task.FromResult<IReadOnlyDictionary<string, string>>(errors);
    }

    public virtual async Task<CliConfigUpdateResult> UpdateAsync(string sectionId, IReadOnlyDictionary<string, JsonNode?> changes,
        string? expectedVersion, CliConfigContext context, CancellationToken cancellationToken = default)
    {
        var section = Section(sectionId) ?? throw new ArgumentException($"unknown config section: {Id}.{sectionId}");
        await ReadyAsync();
        long? expected = null;
        if (expectedVersion != null)
        {
            if (!long.TryParse(expectedVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                throw new CliConfigConflictException((await StoredAsync(sectionId, cancellationToken)).Version.ToString(CultureInfo.InvariantCulture));
            expected = parsed;
        }
        var stored = await Store.WriteAsync(Id, sectionId, changes, expected, cancellationToken);
        lock (_lock) _cache[sectionId] = stored;
        var applied = changes.Keys.ToList();
        var requiresRestart = false;
        foreach (var key in applied)
        {
            var setting = section.Settings.FirstOrDefault(s => s.Key == key);
            if (setting == null) continue;
            if (setting.RequiresRestart) requiresRestart = true;
            setting.Apply?.Invoke(GetValue(sectionId, key));
        }
        return new CliConfigUpdateResult
        {
            Applied = applied,
            RequiresRestart = requiresRestart,
            Version = stored.Version.ToString(CultureInfo.InvariantCulture),
        };
    }
}

/// <summary>
/// The <c>custom</c> provider: one free key/value section (<c>custom</c>) for the host's own settings, in its own
/// <c>store</c> or the server's default one. Values are strings, numbers, booleans, string lists or JSON.
/// </summary>
public static class CliCustomConfigProvider
{
    public static CliStoredConfigProvider Create(string id = "custom", string title = "Custom",
        string description = "Free settings of the host application, kept in the config store", int order = 1000,
        ICliConfigStore? store = null) =>
        new(new CliStoredConfigProviderOptions
        {
            Id = id,
            Title = title,
            Description = description,
            Order = order,
            Store = store,
            Sections = [new CliConfigSectionDefinition { Id = "custom", Title = "Custom settings", AllowCustomKeys = true }],
        });
}
