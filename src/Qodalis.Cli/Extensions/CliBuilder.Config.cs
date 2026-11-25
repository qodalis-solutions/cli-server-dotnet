using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Config;

namespace Qodalis.Cli.Extensions;

/// <summary>Options of the config providers (<see cref="CliBuilder.ConfigureConfig"/>).</summary>
public sealed class CliConfigOptions
{
    /// <summary>Register the <c>config</c> command (default true).</summary>
    public bool Command { get; set; } = true;

    /// <summary>Which environment variables and configuration keys the <c>host</c> provider lists (secrets masked).</summary>
    public CliHostEnvironmentOptions Environment { get; set; } = new();

    /// <summary>
    /// A store per provider id, winning over the provider's own and the default store:
    /// <c>o.Stores["jobs"] = redisStore</c>; same as <c>cli.SetConfigStore(store, id)</c> (which wins for the same id).
    /// A store may be given to several providers.
    /// </summary>
    public Dictionary<string, ICliConfigStore> Stores { get; } = new(StringComparer.Ordinal);
}

/// <summary>A store the host set for one provider id (<see cref="CliBuilder.SetConfigStore(ICliConfigStore, string)"/>), built from the server's services.</summary>
public sealed class CliConfigStoreRegistration
{
    public CliConfigStoreRegistration(string providerId, Func<IServiceProvider, ICliConfigStore> factory)
    {
        if (string.IsNullOrEmpty(providerId) || !CliConfigConstants.ProviderId.IsMatch(providerId))
            throw new ArgumentException($"invalid config provider id: {providerId}");
        ProviderId = providerId;
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public string ProviderId { get; }
    public Func<IServiceProvider, ICliConfigStore> Factory { get; }
}

/// <summary>One <see cref="AppSettingsConfigStore"/> per settings file and section, shared by the providers that use it.</summary>
internal sealed class CliAppSettingsConfigStores
{
    private readonly Dictionary<(string, string), AppSettingsConfigStore> _stores = new();

    public AppSettingsConfigStore Get(AppSettingsConfigStoreOptions options)
    {
        var store = new AppSettingsConfigStore(options);
        lock (_stores)
        {
            var key = (Path.GetFullPath(store.FilePath), string.Join(':', store.SectionPath));
            if (_stores.TryGetValue(key, out var existing)) return existing;
            _stores[key] = store;
            return store;
        }
    }
}

public partial class CliBuilder
{
    /// <summary>Options of the config providers.</summary>
    public CliConfigOptions ConfigOptions { get; } = new();

    /// <summary>
    /// Registers a config provider: its sections are listed by <c>GET /api/v1/qcli/config</c> (admin plugin), the
    /// <c>config</c> command and the <c>config_*</c> MCP tools; a provider that also implements
    /// <see cref="ICliConfigWriter"/> can be changed. A <see cref="CliStoredConfigProvider"/> keeps its changes in its
    /// config store: the host's store for its id, else its own, else the server's default store. Ids are unique: a duplicate throws <c>config provider already registered: &lt;id&gt;</c>
    /// when the server starts; a built-in provider with the same id is replaced.
    /// </summary>
    /// <example>
    /// <code>
    /// cli.AddConfigProvider&lt;BillingConfigProvider&gt;();
    /// </code>
    /// </example>
    public CliBuilder AddConfigProvider<T>() where T : class, ICliConfigProvider
    {
        Services.AddSingleton<ICliConfigProvider, T>();
        return this;
    }

    /// <summary>Registers a config provider instance (see <see cref="AddConfigProvider{T}"/>).</summary>
    public CliBuilder AddConfigProvider(ICliConfigProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Services.AddSingleton(provider);
        return this;
    }

    /// <summary>Registers a config provider built from the server's services (see <see cref="AddConfigProvider{T}"/>).</summary>
    public CliBuilder AddConfigProvider(Func<IServiceProvider, ICliConfigProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.AddSingleton(factory);
        return this;
    }

    /// <summary>
    /// The default store: keeps the writable settings of every provider without a store of its own in
    /// <paramref name="store"/> (default in memory, lost on restart): a <see cref="JsonFileConfigStore"/>, an
    /// <see cref="AppSettingsConfigStore"/>, the database store of <c>Qodalis.Cli.Plugin.Config.EfCore</c>, the Redis store
    /// of <c>Qodalis.Cli.Plugin.Config.Redis</c>, ...
    /// </summary>
    /// <example>
    /// <code>
    /// cli.SetConfigStore(new JsonFileConfigStore("qcli-config.json"));   // every provider
    /// cli.SetConfigStore&lt;EfCoreConfigStore&gt;("jobs");                  // jobs only, from the container
    /// cli.SetAppSettingsConfigStore("billing");                           // billing in appsettings.json
    /// </code>
    /// </example>
    public CliBuilder SetConfigStore(ICliConfigStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Services.RemoveAll<ICliConfigStore>();
        Services.AddSingleton(store);
        return this;
    }

    /// <summary>The default store, built from the server's services (see <see cref="SetConfigStore(ICliConfigStore)"/>).</summary>
    public CliBuilder SetConfigStore(Func<IServiceProvider, ICliConfigStore> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.RemoveAll<ICliConfigStore>();
        Services.AddSingleton(factory);
        return this;
    }

    /// <summary>
    /// The default store, resolved from the container (registered as a singleton when it is not yet; one instance shared
    /// with every provider id given the same type). See <see cref="SetConfigStore(ICliConfigStore)"/>.
    /// </summary>
    public CliBuilder SetConfigStore<TStore>() where TStore : class, ICliConfigStore
    {
        Services.TryAddSingleton<TStore>();
        return SetConfigStore(sp => sp.GetRequiredService<TStore>());
    }

    /// <summary>
    /// Keeps the changes of the provider <paramref name="providerId"/> in <paramref name="store"/>, whatever its own
    /// store; the other providers keep theirs. One store may serve several providers (their data never mixes).
    /// </summary>
    /// <example>
    /// <code>
    /// cli.SetConfigStore(new RedisConfigStore(new() { Url = "redis://cache:6379/0" }), "jobs");
    /// </code>
    /// </example>
    public CliBuilder SetConfigStore(ICliConfigStore store, string providerId)
    {
        ArgumentNullException.ThrowIfNull(store);
        return SetConfigStore(_ => store, providerId);
    }

    /// <summary>The store of one provider, built from the server's services (see <see cref="SetConfigStore(ICliConfigStore, string)"/>).</summary>
    public CliBuilder SetConfigStore(Func<IServiceProvider, ICliConfigStore> factory, string providerId)
    {
        Services.AddSingleton(new CliConfigStoreRegistration(providerId, factory));
        return this;
    }

    /// <summary>
    /// The store of one provider, resolved from the container (registered as a singleton when it is not yet, so every
    /// provider given <typeparamref name="TStore"/> shares one instance). See <see cref="SetConfigStore(ICliConfigStore, string)"/>.
    /// </summary>
    public CliBuilder SetConfigStore<TStore>(string providerId) where TStore : class, ICliConfigStore
    {
        Services.TryAddSingleton<TStore>();
        return SetConfigStore(sp => sp.GetRequiredService<TStore>(), providerId);
    }

    /// <summary>
    /// Keeps writable settings in the app's <c>appsettings.json</c> (in the host's content root), section
    /// <c>Qcli:Config</c> (<see cref="AppSettingsConfigStore"/>): of <paramref name="providerId"/>, or of every provider
    /// without its own store when null. <paramref name="configure"/> picks another file
    /// (<c>o.Environment = "Development"</c> for <c>appsettings.Development.json</c>) or section. Providers given the same
    /// file and section share one store.
    /// </summary>
    public CliBuilder SetAppSettingsConfigStore(string? providerId = null, Action<AppSettingsConfigStoreOptions>? configure = null)
    {
        Services.TryAddSingleton<CliAppSettingsConfigStores>();
        ICliConfigStore Create(IServiceProvider sp)
        {
            var options = new AppSettingsConfigStoreOptions { ContentRoot = sp.GetService<IHostEnvironment>()?.ContentRootPath };
            configure?.Invoke(options);
            return sp.GetRequiredService<CliAppSettingsConfigStores>().Get(options);
        }
        return providerId == null ? SetConfigStore(Create) : SetConfigStore(Create, providerId);
    }

    /// <summary>Configures the config providers (the <c>config</c> command, what the <c>host</c> provider lists, a store per provider id).</summary>
    public CliBuilder ConfigureConfig(Action<CliConfigOptions> configure)
    {
        configure(ConfigOptions);
        return this;
    }
}
