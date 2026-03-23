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
    public List<DataExplorerSourceInfo> GetSources()
    {
        return _providers.Values.Select(e => new DataExplorerSourceInfo
        {
            Name = e.Options.Name,
            Description = e.Options.Description,
            Language = e.Options.Language,
            DefaultOutputFormat = e.Options.DefaultOutputFormat,
            Templates = e.Options.Templates,
            Parameters = e.Options.Parameters
        }).ToList();
    }
}
