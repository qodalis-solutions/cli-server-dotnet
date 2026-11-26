using Qodalis.Cli.Abstractions.DataExplorer;

namespace Qodalis.Cli.Services;

/// <summary>
/// Holds the registration details for a data explorer provider (either by type or by instance).
/// </summary>
public class DataExplorerProviderRegistration
{
    /// <summary>Gets or sets the provider type for deferred resolution from the DI container.</summary>
    public Type? ProviderType { get; set; }

    /// <summary>Gets or sets a pre-created provider instance.</summary>
    public IDataExplorerProvider? ProviderInstance { get; set; }

    /// <summary>Gets or sets the provider configuration options.</summary>
    public required DataExplorerProviderOptions Options { get; set; }

    /// <summary>
    /// A source built from settings that load after the services are registered (a plugin's): called when the data
    /// explorer is first used; null leaves the source out (its settings missing). Its options replace <see cref="Options"/>.
    /// </summary>
    public Func<(IDataExplorerProvider Provider, DataExplorerProviderOptions Options)?>? Deferred { get; set; }
}

/// <summary>
/// Rules of the data explorer plugin's entry for the sources added after it (<c>cli.AddPlugin</c> or the dashboard):
/// read-only for all, a row cap, a rate limit for sources without their own. Read when the data explorer is first used.
/// </summary>
public sealed class DataExplorerSourcePolicy
{
    /// <summary>The number of source registrations before it: those keep their own rules.</summary>
    public required int After { get; init; }

    public required Func<bool> ForceReadOnly { get; init; }

    /// <summary>The most rows any source returns; 0: each its own.</summary>
    public required Func<int> MaxRows { get; init; }

    public required Func<DataExplorerRateLimit?> RateLimit { get; init; }

    /// <summary>Applies the rules to a source's options.</summary>
    public void Apply(DataExplorerProviderOptions options)
    {
        if (ForceReadOnly()) options.ReadOnly = true;
        var max = MaxRows();
        if (max > 0 && (options.MaxRows <= 0 || options.MaxRows > max)) options.MaxRows = max;
        if (options.RateLimit == null && RateLimit() is { } limit) options.RateLimit = limit;
    }
}

/// <summary>
/// Internal container pairing a resolved provider with its options.
/// </summary>
internal class RegisteredProvider
{
    public required IDataExplorerProvider Provider { get; set; }
    public required DataExplorerProviderOptions Options { get; set; }
}

/// <summary>
/// Registry for data explorer providers, supporting lookup by source name.
/// </summary>
public class DataExplorerRegistry : IDataExplorerRegistry
{
    private readonly Dictionary<string, RegisteredProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a data explorer provider with its options.
    /// </summary>
    /// <param name="provider">The provider instance.</param>
    /// <param name="options">The provider options containing the source name.</param>
    internal void Register(IDataExplorerProvider provider, DataExplorerProviderOptions options)
    {
        _providers[options.Name] = new RegisteredProvider
        {
            Provider = provider,
            Options = options
        };
    }

    /// <inheritdoc />
    public (IDataExplorerProvider Provider, DataExplorerProviderOptions Options)? Get(string name)
    {
        if (_providers.TryGetValue(name, out var entry))
        {
            return (entry.Provider, entry.Options);
        }

        return null;
    }

    /// <inheritdoc />
    public List<DataExplorerSourceInfo> GetSources() => GetSources(null);

    /// <inheritdoc />
    public List<DataExplorerSourceInfo> GetSources(DataExplorerCaller? caller)
    {
        return _providers.Values.Where(e => DataExplorerPolicy.CanUseSource(e.Options, caller)).Select(e => new DataExplorerSourceInfo
        {
            Name = e.Options.Name,
            Description = e.Options.Description,
            Language = e.Options.Language,
            DefaultOutputFormat = e.Options.DefaultOutputFormat,
            Templates = e.Options.Templates,
            Parameters = e.Options.Parameters,
            ReadOnly = e.Options.ReadOnly,
            CanExport = DataExplorerPolicy.CanExport(e.Options, caller)
        }).ToList();
    }
}
