using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Abstractions.Config;

namespace Qodalis.Cli.Config;

/// <summary>
/// The config providers of a server and the store their writable settings are kept in (<c>cli.SetConfigStore</c>, in
/// memory by default). Started when the server starts: the store is attached to the providers registered so far, and to
/// every provider registered later, and the stored values are applied.
/// </summary>
public sealed class CliConfigRegistry
{
    private readonly List<ICliConfigProvider> _providers = [];
    private readonly List<Task> _loading = [];
    private readonly object _lock = new();
    private readonly ILogger _logger;
    private bool _started;

    public CliConfigRegistry(ICliConfigStore? store = null, ILogger? logger = null)
    {
        Store = store ?? new InMemoryConfigStore();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The store of writable settings.</summary>
    public ICliConfigStore Store { get; }

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

    /// <summary>Attaches the store to the providers (once); providers added later are attached when added.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
            foreach (var provider in _providers) Attach(provider);
        }
    }

    /// <summary>Completes when every provider has loaded and applied its stored settings; throws the first failure.</summary>
    public Task ReadyAsync()
    {
        lock (_lock) return Task.WhenAll(_loading.ToArray());
    }

    private void Attach(ICliConfigProvider provider)
    {
        if (provider is not ICliConfigStoreAttachable attachable) return;
        Task loading;
        try
        {
            loading = attachable.AttachStoreAsync(Store);
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
