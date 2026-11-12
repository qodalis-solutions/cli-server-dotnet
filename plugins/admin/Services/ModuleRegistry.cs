using System.Collections.Concurrent;
using System.Reflection;
using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>
/// Tracks registered modules and their enabled/disabled state.
/// Implements <see cref="ICliProcessorFilter"/> to block execution of processors belonging to disabled modules.
/// Commands registered on their own (<c>AddProcessor</c>, built by dependency injection) join the module their
/// <see cref="ICliCommandProcessor.Module"/> names: a module added with that name (it may have no processors of its
/// own, only describe the group: description, version, icon), or else a module of that name made from the commands.
/// </summary>
public class ModuleRegistry : ICliProcessorFilter
{
    private readonly IReadOnlyList<ICliModule> _modules;
    private readonly Func<IEnumerable<ICliCommandProcessor>> _standaloneProcessors;
    private readonly Lazy<IReadOnlyList<ModuleEntry>> _entries;
    private readonly Lazy<Dictionary<ICliCommandProcessor, string>> _processorToModule;
    private readonly ConcurrentDictionary<string, bool> _enabledState = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="ModuleRegistry"/> class with the registered modules.
    /// </summary>
    /// <param name="modules">The list of registered CLI modules.</param>
    /// <param name="processors">
    /// Every registered command processor (read on first use), so commands added on their own are grouped by
    /// their <see cref="ICliCommandProcessor.Module"/>; <c>null</c> = only the modules' own processors.
    /// </param>
    public ModuleRegistry(IReadOnlyList<ICliModule> modules, Func<IEnumerable<ICliCommandProcessor>>? processors = null)
    {
        _modules = modules;
        _standaloneProcessors = processors ?? (() => []);
        _entries = new Lazy<IReadOnlyList<ModuleEntry>>(BuildEntries);
        _processorToModule = new Lazy<Dictionary<ICliCommandProcessor, string>>(() =>
        {
            var map = new Dictionary<ICliCommandProcessor, string>(ReferenceEqualityComparer.Instance);
            foreach (var entry in _entries.Value)
            {
                foreach (var processor in entry.Processors) map.TryAdd(processor, entry.Name);
            }
            return map;
        });
    }

    private sealed record ModuleEntry(
        string Name, string Version, string Description, string Author, string? Icon, List<ICliCommandProcessor> Processors);

    private IReadOnlyList<ModuleEntry> BuildEntries()
    {
        var entries = _modules
            .Select(m => new ModuleEntry(m.Name, m.Version, m.Description, m.Author?.Name ?? "Unknown", m.Icon, m.Processors.ToList()))
            .ToList();
        var owned = new HashSet<ICliCommandProcessor>(entries.SelectMany(e => e.Processors), ReferenceEqualityComparer.Instance);
        foreach (var group in _standaloneProcessors()
                     .Where(p => !owned.Contains(p) && !string.IsNullOrWhiteSpace(p.Module))
                     .GroupBy(p => p.Module!, StringComparer.OrdinalIgnoreCase))
        {
            if (entries.FirstOrDefault(e => e.Name.Equals(group.Key, StringComparison.OrdinalIgnoreCase)) is { } module)
            {
                module.Processors.AddRange(group);
                continue;
            }
            var assembly = group.First().GetType().Assembly;
            entries.Add(new ModuleEntry(
                group.Key,
                (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? assembly.GetName().Version?.ToString() ?? string.Empty).Split('+')[0],
                assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? string.Empty,
                assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? string.Empty,
                null,
                group.ToList()));
        }
        return entries;
    }

    /// <inheritdoc />
    public bool IsAllowed(ICliCommandProcessor processor) =>
        !_processorToModule.Value.TryGetValue(processor, out var moduleName) || IsEnabled(moduleName);

    private PluginInfo Info(ModuleEntry m) => new()
    {
        Id = m.Name,
        Name = m.Name,
        Version = m.Version,
        Description = m.Description,
        Author = m.Author,
        Icon = m.Icon,
        Enabled = IsEnabled(m.Name),
        ProcessorCount = m.Processors.Count,
        Processors = m.Processors.Select(p => p.Command).ToList(),
    };

    private ModuleEntry? Find(string id) =>
        _entries.Value.FirstOrDefault(m => m.Name.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns information about all registered plugin modules.
    /// </summary>
    public List<PluginInfo> List() => _entries.Value.Select(Info).ToList();

    /// <summary>
    /// Gets information about a specific plugin module by ID.
    /// </summary>
    /// <param name="id">The module name (case-insensitive).</param>
    /// <returns>The plugin info, or null if not found.</returns>
    public PluginInfo? GetById(string id) => Find(id) is { } module ? Info(module) : null;

    /// <summary>
    /// Toggles the enabled/disabled state of a plugin module.
    /// </summary>
    /// <param name="id">The module name (case-insensitive).</param>
    /// <returns>The toggle result with the new state, or null if not found.</returns>
    public ToggleResult? Toggle(string id)
    {
        if (Find(id) is not { } module) return null;

        _enabledState.AddOrUpdate(module.Name, _ => false, (_, current) => !current);
        return new ToggleResult { Enabled = IsEnabled(module.Name) };
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
    /// <summary>One emoji or an image URL for the module (<see cref="ICliModule.Icon"/>); <c>null</c> when it has none.</summary>
    public string? Icon { get; set; }
    /// <summary>Whether the module is currently enabled.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Number of command processors provided by this module.</summary>
    public int ProcessorCount { get; set; }
    /// <summary>List of command names registered by this module.</summary>
    public List<string> Processors { get; set; } = new();
}
