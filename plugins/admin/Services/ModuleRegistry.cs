using System.Collections.Concurrent;
using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>
/// Tracks registered modules and their enabled/disabled state.
/// </summary>
public class ModuleRegistry
{
    private readonly IReadOnlyList<ICliModule> _modules;
    private readonly ConcurrentDictionary<string, bool> _enabledState = new();

    public ModuleRegistry(IReadOnlyList<ICliModule> modules)
    {
        _modules = modules;
        foreach (var module in modules)
        {
            _enabledState.TryAdd(module.Name, true);
        }
    }

    public List<PluginInfo> List()
    {
        return _modules.Select(m => new PluginInfo
        {
            Id = m.Name,
            Name = m.Name,
            Version = m.Version,
            Description = m.Description,
            Author = m.Author?.Name ?? "Unknown",
            Enabled = _enabledState.GetValueOrDefault(m.Name, true),
            ProcessorCount = m.Processors.Count(),
            Processors = m.Processors.Select(p => p.Command).ToList(),
        }).ToList();
    }

    public PluginInfo? GetById(string id)
    {
        var module = _modules.FirstOrDefault(m =>
            m.Name.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (module == null) return null;

        return new PluginInfo
        {
            Id = module.Name,
            Name = module.Name,
            Version = module.Version,
            Description = module.Description,
            Author = module.Author?.Name ?? "Unknown",
            Enabled = _enabledState.GetValueOrDefault(module.Name, true),
            ProcessorCount = module.Processors.Count(),
            Processors = module.Processors.Select(p => p.Command).ToList(),
        };
    }

    public ToggleResult? Toggle(string id)
    {
        var module = _modules.FirstOrDefault(m =>
            m.Name.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (module == null) return null;

        _enabledState.AddOrUpdate(module.Name, _ => false, (_, current) => !current);
        var newState = IsEnabled(module.Name);

        string? warning = null;
        if (!newState)
        {
            warning = "Note: Toggling a module off tracks state only. Already-registered command processors remain active until the server is restarted.";
        }

        return new ToggleResult { Enabled = newState, Warning = warning };
    }

    public bool IsEnabled(string id)
    {
        return _enabledState.GetValueOrDefault(id, true);
    }
}

public class ToggleResult
{
    public bool Enabled { get; set; }
    public string? Warning { get; set; }
}

public class PluginInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int ProcessorCount { get; set; }
    public List<string> Processors { get; set; } = new();
}
