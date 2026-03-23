using System.Collections.Concurrent;
using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>
/// Tracks registered modules and their enabled/disabled state.
/// Implements <see cref="ICliProcessorFilter"/> to block execution of processors belonging to disabled modules.
/// </summary>
public class ModuleRegistry : ICliProcessorFilter
{
    private readonly IReadOnlyList<ICliModule> _modules;
    private readonly ConcurrentDictionary<string, bool> _enabledState = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ModuleRegistry"/> class with the registered modules.
    /// </summary>
    /// <param name="modules">The list of registered CLI modules.</param>
    private readonly Dictionary<ICliCommandProcessor, string> _processorToModule = new();

    public ModuleRegistry(IReadOnlyList<ICliModule> modules)
    {
        _modules = modules;
        foreach (var module in modules)
        {
            _enabledState.TryAdd(module.Name, true);
            foreach (var processor in module.Processors)
            {
                _processorToModule[processor] = module.Name;
            }
        }
    }

    /// <inheritdoc />
    public bool IsAllowed(ICliCommandProcessor processor)
    {
        if (_processorToModule.TryGetValue(processor, out var moduleName))
        {
            return IsEnabled(moduleName);
        }

        return true;
    }

    /// <summary>
    /// Returns information about all registered plugin modules.
    /// </summary>
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

    /// <summary>
    /// Gets information about a specific plugin module by ID.
    /// </summary>
    /// <param name="id">The module name (case-insensitive).</param>
    /// <returns>The plugin info, or null if not found.</returns>
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

    /// <summary>
    /// Toggles the enabled/disabled state of a plugin module.
    /// </summary>
    /// <param name="id">The module name (case-insensitive).</param>
    /// <returns>The toggle result with the new state, or null if not found.</returns>
    public ToggleResult? Toggle(string id)
    {
        var module = _modules.FirstOrDefault(m =>
            m.Name.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (module == null) return null;

        _enabledState.AddOrUpdate(module.Name, _ => false, (_, current) => !current);
        var newState = IsEnabled(module.Name);

        return new ToggleResult { Enabled = newState };
    }

    /// <summary>
    /// Checks whether a plugin module is currently enabled.
    /// </summary>
    /// <param name="id">The module name.</param>
    /// <returns>True if the module is enabled; defaults to true for unknown modules.</returns>
    public bool IsEnabled(string id)
    {
        return _enabledState.GetValueOrDefault(id, true);
    }
}

/// <summary>
/// Result of toggling a plugin module's enabled state.
/// </summary>
public class ToggleResult
{
    /// <summary>Whether the module is now enabled.</summary>
    public bool Enabled { get; set; }
    /// <summary>Optional warning message (e.g., noting that processors remain active until restart).</summary>
    public string? Warning { get; set; }
}

/// <summary>
/// Describes a registered plugin module and its current state.
/// </summary>
public class PluginInfo
{
    /// <summary>Unique identifier (same as module name).</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>Human-readable module name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Module version string.</summary>
    public string Version { get; set; } = string.Empty;
    /// <summary>Module description.</summary>
    public string Description { get; set; } = string.Empty;
    /// <summary>Module author name.</summary>
    public string Author { get; set; } = string.Empty;
    /// <summary>Whether the module is currently enabled.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Number of command processors provided by this module.</summary>
    public int ProcessorCount { get; set; }
    /// <summary>List of command names registered by this module.</summary>
    public List<string> Processors { get; set; } = new();
}
