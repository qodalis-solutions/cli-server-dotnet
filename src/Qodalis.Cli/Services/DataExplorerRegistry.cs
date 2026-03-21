using Qodalis.Cli.Abstractions.DataExplorer;

namespace Qodalis.Cli.Services;

public class DataExplorerProviderRegistration
{
    public Type? ProviderType { get; set; }
    public IDataExplorerProvider? ProviderInstance { get; set; }
    public required DataExplorerProviderOptions Options { get; set; }
}

internal class RegisteredProvider
{
    public required IDataExplorerProvider Provider { get; set; }
    public required DataExplorerProviderOptions Options { get; set; }
}

public class DataExplorerRegistry
{
    private readonly Dictionary<string, RegisteredProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    internal void Register(IDataExplorerProvider provider, DataExplorerProviderOptions options)
    {
        _providers[options.Name] = new RegisteredProvider
        {
            Provider = provider,
            Options = options
        };
    }

    public (IDataExplorerProvider Provider, DataExplorerProviderOptions Options)? Get(string name)
    {
        if (_providers.TryGetValue(name, out var entry))
        {
            return (entry.Provider, entry.Options);
        }

        return null;
    }

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
