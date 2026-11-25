using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Abstractions.Config;

namespace Qodalis.Cli.Config;

/// <summary>
/// The config providers of a server and the stores their writable settings are kept in. Every provider that keeps
/// settings in a store (<see cref="ICliConfigStoreAttachable"/>) gets one, resolved in this order: the host's store for
/// its id (<c>cli.SetConfigStore(store, id)</c>, <c>ConfigureConfig(o =&gt; o.Stores[id] = store)</c>,
/// <see cref="SetStore"/>), the provider's own (<see cref="ICliConfigStoreAttachable.OwnStore"/>), the default store
/// (<c>cli.SetConfigStore(store)</c>), else in memory. A store may serve several providers. Started when the server
/// starts: the providers registered so far get their store, every provider registered later gets it when added, and the
/// stored values are applied.
/// </summary>
public sealed class CliConfigRegistry
{
    private readonly List<ICliConfigProvider> _providers = [];
    private readonly List<Task> _loading = [];
    private readonly Dictionary<string, ICliConfigStore> _stores = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ICliConfigStore> _attached = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly ILogger _logger;
    private ICliConfigStore _defaultStore;
    private bool _started;

    public CliConfigRegistry(ICliConfigStore? defaultStore = null, ILogger? logger = null)
    {
        _defaultStore = defaultStore ?? new InMemoryConfigStore();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The store providers use unless the host set one for their id or they have their own (in memory unless set).</summary>
    public ICliConfigStore DefaultStore
    {
        get
        {
            lock (_lock) return _defaultStore;
        }
    }

    /// <summary>The stores the host set per provider id (<see cref="SetStore"/>).</summary>
    public IReadOnlyDictionary<string, ICliConfigStore> ProviderStores
    {
        get
        {
            lock (_lock) return new Dictionary<string, ICliConfigStore>(_stores, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Keeps writable settings in <paramref name="store"/>: of every provider without a store of its own (the default
    /// store, before the registry starts), or, with <paramref name="providerId"/>, of that provider whatever its own
    /// store (before it is registered, also after the server started). Too late throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public void SetStore(ICliConfigStore store, string? providerId = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (_lock)
        {
            if (providerId == null)
            {
                if (_started) throw new InvalidOperationException("the default config store must be set before the server starts");
                _defaultStore = store;
                return;
            }
            if (!CliConfigConstants.ProviderId.IsMatch(providerId))
                throw new ArgumentException($"invalid config provider id: {providerId}");
            if (_attached.ContainsKey(providerId))
                throw new InvalidOperationException($"the config store of {providerId} must be set before the provider is registered");
            _stores[providerId] = store;
        }
    }

    /// <summary>
    /// The store a provider's settings are kept in: the one it was given when registered, else the one it would get
    /// now; null for a provider that keeps none (read-only, or saving itself).
    /// </summary>
    public ICliConfigStore? StoreOf(ICliConfigProvider provider)
    {
        if (provider is not ICliConfigStoreAttachable attachable) return null;
        lock (_lock) return _attached.TryGetValue(provider.Id, out var attached) ? attached : Resolve(provider.Id, attachable);
    }

    /// <summary>The store of the provider with this id (see <see cref="StoreOf(ICliConfigProvider)"/>); null when unknown.</summary>
    public ICliConfigStore? StoreOf(string providerId) => Find(providerId) is { } provider ? StoreOf(provider) : null;

    /// <summary>Every store in use (each once), the default first.</summary>
    public IReadOnlyList<ICliConfigStore> Stores
    {
        get
        {
            lock (_lock)
            {
                var all = new List<ICliConfigStore> { _defaultStore };
                foreach (var provider in _providers)
                {
                    if (provider is not ICliConfigStoreAttachable attachable) continue;
                    var store = _attached.TryGetValue(provider.Id, out var attached) ? attached : Resolve(provider.Id, attachable);
                    if (!all.Contains(store)) all.Add(store);
                }
                return all;
            }
        }
    }

    /// <summary>The resolution order: the host's store for the id, the provider's own, the default store.</summary>
    private ICliConfigStore Resolve(string providerId, ICliConfigStoreAttachable provider) =>
        _stores.TryGetValue(providerId, out var hosts) ? hosts : provider.OwnStore ?? _defaultStore;

    /// <summary>Registers a provider. Throws <c>invalid config provider: &lt;id&gt;</c> or <c>config provider already registered: &lt;id&gt;</c>.</summary>
    public void Add(ICliConfigProvider provider)
    {
        if (provider == null || string.IsNullOrEmpty(provider.Id) || !CliConfigConstants.ProviderId.IsMatch(provider.Id))
            throw new ArgumentException($"invalid config provider: {provider?.Id}");
        if (string.IsNullOrWhiteSpace(provider.Title))
            throw new ArgumentException($"invalid config provider: {provider.Id} needs a title");
        lock (_lock)
        {
            if (_providers.Any(p => p.Id == provider.Id))
                throw new InvalidOperationException($"config provider already registered: {provider.Id}");
            _providers.Add(provider);
            if (_started) Attach(provider);
        }
    }

    /// <summary>True when a provider with this id is registered.</summary>
    public bool Has(string id)
    {
        lock (_lock) return _providers.Any(p => p.Id == id);
    }

    public ICliConfigProvider? Find(string id)
    {
        lock (_lock) return _providers.FirstOrDefault(p => p.Id == id);
    }

    /// <summary>The providers sorted by order (default 100), then id.</summary>
    public IReadOnlyList<ICliConfigProvider> Providers
    {
        get
        {
            lock (_lock)
            {
                return _providers.OrderBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
            }
        }
    }

    public bool Started
    {
        get
        {
            lock (_lock) return _started;
        }
    }

    /// <summary>Gives the providers their store (once); providers added later get theirs when added.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
            foreach (var provider in _providers) Attach(provider);
        }
    }

    /// <summary>
    /// Completes when every store is ready (<see cref="ICliConfigStore.ReadyAsync"/>) and every provider has loaded and
    /// applied its stored settings; throws the first failure.
    /// </summary>
    public Task ReadyAsync()
    {
        var stores = Stores;
        lock (_lock) return Task.WhenAll(stores.Select(StoreReady).Concat(_loading).ToArray());
    }

    private static Task StoreReady(ICliConfigStore store)
    {
        try
        {
            return store.ReadyAsync();
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    private void Attach(ICliConfigProvider provider)
    {
        if (provider is not ICliConfigStoreAttachable attachable) return;
        var store = Resolve(provider.Id, attachable);
        _attached[provider.Id] = store;
        Task loading;
        try
        {
            loading = attachable.AttachStoreAsync(store);
        }
        catch (Exception ex)
        {
            loading = Task.FromException(ex);
        }
        // Surfaced by ReadyAsync (and every config request); logged once here
        loading.ContinueWith(t => _logger.LogError(t.Exception?.GetBaseException(),
                "Loading the settings of config provider '{Provider}' failed", provider.Id),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        _loading.Add(loading);
    }
}
