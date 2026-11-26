using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.Restart;
using Qodalis.Cli.Config;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Models;

namespace Qodalis.Cli.Plugins;

/// <summary>Where the runtime-installed plugins are kept and whether they are loaded (<c>cli.ConfigureServerPlugins(o =&gt; ...)</c>).</summary>
public sealed class CliServerPluginsOptions
{
    /// <summary>The server's data directory unless set: <c>qcli-data</c> in the working directory.</summary>
    public const string DefaultDataDir = "qcli-data";

    /// <summary>False: installed plugins are not loaded, and the server marketplace cannot install any.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The server's data directory (default <c>&lt;working directory&gt;/qcli-data</c>).</summary>
    public string? DataDir { get; set; }

    /// <summary>The plugins directory (default <c>&lt;data dir&gt;/server-plugins</c>).</summary>
    public string? Dir { get; set; }
}

/// <summary>
/// Loads a plugin's assemblies in a collectible context of its own; an assembly the host has (<c>Qodalis.Cli</c>, its
/// abstractions, the framework, any package the host references) resolves to the host's copy, so there is one copy of the
/// contracts and of every shared type.
/// </summary>
public sealed class CliPluginLoadContext(string name, string dir) : AssemblyLoadContext($"qcli-plugin:{name}", isCollectible: true)
{
    private static readonly Lazy<HashSet<string>> HostAssemblies = new(() =>
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                names.Add(Path.GetFileNameWithoutExtension(path));
        return names;
    });

    /// <summary>True when the host has the assembly (its dependencies, or loaded already).</summary>
    public static bool HostHas(string assemblyName) =>
        HostAssemblies.Value.Contains(assemblyName)
        || Default.Assemblies.Any(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));

    public string Dir { get; } = dir;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name == null || HostHas(name)) return null;
        var path = Path.Combine(Dir, name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = Path.Combine(Dir, unmanagedDllName);
        return File.Exists(path) ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}

/// <summary>The restart service until the container exists (the server sets it at startup).</summary>
internal sealed class CliDeferredRestart : ICliServerRestart
{
    public ICliServerRestart? Target { get; set; }

    private ICliServerRestart Real => Target ?? throw new InvalidOperationException("the restart service is available once the server started");

    public string StartedAt => Real.StartedAt;
    public bool Restarting => Real.Restarting;
    public CliServerRestartStatus Status() => Real.Status();
    public IReadOnlyList<CliRestartRequirement> Pending() => Real.Pending();
    public void MarkRequired(CliRestartRequirementInput requirement) => Real.MarkRequired(requirement);
    public bool ClearRequired(string id) => Real.ClearRequired(id);

    public Task<CliServerRestartResult> RestartAsync(CliServerRestartRequest? request = null, CancellationToken cancellationToken = default) =>
        Real.RestartAsync(request, cancellationToken);
}

/// <summary>A plugin's logger: buffered until the server's logging exists, then forwarded.</summary>
internal sealed class CliPluginLogger(string category) : ILogger
{
    private readonly List<(LogLevel Level, string Message, Exception? Error)> _buffer = [];
    private ILogger? _target;

    public void Attach(ILoggerFactory factory)
    {
        List<(LogLevel, string, Exception?)> buffered;
        lock (_buffer)
        {
            _target = factory.CreateLogger(category);
            buffered = [.. _buffer];
            _buffer.Clear();
        }
        foreach (var (level, message, error) in buffered) _target.Log(level, error, "{Message}", message);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _target?.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _target?.IsEnabled(logLevel) ?? logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_buffer)
        {
            if (_target == null)
            {
                if (_buffer.Count < 200) _buffer.Add((logLevel, formatter(state, exception), exception));
                return;
            }
        }
        _target.Log(logLevel, eventId, state, exception, formatter);
    }
}

/// <summary>
/// The server's runtime-installed plugins: <c>&lt;data dir&gt;/server-plugins/installed.json</c> lists them (written by the
/// admin plugin's server marketplace), each one's assemblies are in <c>server-plugins/&lt;package&gt;/&lt;version&gt;/</c>.
/// While the server's services are registered (<c>AddCli</c>, after the host's own), each package's assembly is loaded
/// in a <see cref="CliPluginLoadContext"/>, its <c>[assembly: CliPlugin]</c> entry created and, in their declared
/// order, registered. A plugin that fails is logged and its error kept in <c>installed.json</c>: startup goes on.
/// </summary>
public sealed partial class CliServerPlugins
{
    /// <summary>The plugins directory below the data directory.</summary>
    public const string DirName = "server-plugins";

    /// <summary>The installed list in that directory.</summary>
    public const string InstalledFile = "installed.json";

    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly List<CliServerPluginState> _states = [];
    private readonly List<(CliServerPluginState State, CliStoredConfigProvider Provider)> _settings = [];
    private readonly List<CliPluginLogger> _loggers = [];
    private readonly Dictionary<CliServerPluginState, CliInstalledPlugin> _installed = new();
    private readonly HashSet<string> _ownStoreless = new(StringComparer.Ordinal);
    private ICliConfigStore? _pluginSettingsStore;

    /// <summary>The file of the plugins' settings while the server keeps its settings in memory.</summary>
    public const string SettingsFile = "plugin-settings.json";

    /// <summary>
    /// Where the plugins' settings are kept while the server's default config store is in memory:
    /// <c>&lt;data dir&gt;/plugin-settings.json</c> (mode 0600 on Unix), so a setting saved from the dashboard survives a restart.
    /// </summary>
    public ICliConfigStore PluginSettingsStore =>
        _pluginSettingsStore ??= new JsonFileConfigStore(Path.Combine(DataDir, SettingsFile));

    /// <summary>The settings providers of the registered plugins that have no store of their own.</summary>
    internal IReadOnlyList<string> StorelessSettings()
    {
        lock (_lock) return [.. _ownStoreless];
    }
    private readonly object _lock = new();
    private bool _loaded;

    public CliServerPlugins(CliServerPluginsOptions? options = null)
    {
        options ??= new CliServerPluginsOptions();
        Enabled = options.Enabled;
        DataDir = Path.GetFullPath(options.DataDir ?? Path.Combine(Directory.GetCurrentDirectory(), CliServerPluginsOptions.DefaultDataDir));
        Dir = Path.GetFullPath(options.Dir ?? Path.Combine(DataDir, DirName));
    }

    /// <summary>False when the host turned loading off.</summary>
    public bool Enabled { get; }

    /// <summary>The server's data directory.</summary>
    public string DataDir { get; }

    /// <summary><c>&lt;data dir&gt;/server-plugins</c>.</summary>
    public string Dir { get; }

    internal CliDeferredRestart Restart { get; } = new();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}\z")]
    private static partial Regex PackageId();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,63}\z")]
    private static partial Regex PluginName();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z")]
    private static partial Regex Semver();

    /// <summary>True for a NuGet package id (<c>Qodalis.Cli.Plugin.Weather</c>).</summary>
    public static bool IsValidPackage(string? id) => id != null && PackageId().IsMatch(id) && !id.Contains("..", StringComparison.Ordinal);

    /// <summary>True for a marketplace plugin id (<c>weather</c>, <c>data-explorer-postgres</c>) or a package id.</summary>
    public static bool IsValidName(string? name) => name != null && (PluginName().IsMatch(name) || IsValidPackage(name));

    /// <summary>True for a semver version (NuGet's four-part versions too).</summary>
    public static bool IsValidVersion(string? version) => version != null && version.Length <= 64 && Semver().IsMatch(version);

    /// <summary><c>&lt;dir&gt;/&lt;package&gt;/&lt;version&gt;</c>, inside the plugins directory. Throws <see cref="ArgumentException"/> for a bad id or version.</summary>
    public string VersionDir(string package, string version)
    {
        if (!IsValidPackage(package) || !IsValidVersion(version)) throw new ArgumentException("invalid package");
        var path = Path.GetFullPath(Path.Combine(Dir, package, version));
        if (!path.StartsWith(Dir + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ArgumentException("invalid package");
        return path;
    }

    /// <summary>The installed list (<c>installed.json</c>); empty when there is none or it is not readable.</summary>
    public IReadOnlyList<CliServerPluginRecord> Records()
    {
        var file = Path.Combine(Dir, InstalledFile);
        if (!File.Exists(file)) return [];
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file))?["plugins"] is not JsonArray plugins) return [];
            var records = new List<CliServerPluginRecord>();
            foreach (var node in plugins)
            {
                try
                {
                    var record = node?.Deserialize<CliServerPluginRecord>();
                    if (record != null && IsValidName(record.Name) && IsValidPackage(record.Package) && IsValidVersion(record.Version)
                        && !string.IsNullOrEmpty(record.Integrity))
                        records.Add(record);
                }
                catch (JsonException)
                {
                    // a broken record is left out
                }
            }
            return records;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Writes the installed list atomically (a temporary file moved over it).</summary>
    public void Save(IEnumerable<CliServerPluginRecord> records)
    {
        Directory.CreateDirectory(Dir);
        var file = Path.Combine(Dir, InstalledFile);
        var temp = $"{file}.{Environment.ProcessId}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new { plugins = records.ToList() }, FileJson) + "\n", new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, file, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    /// <summary>What this process loaded at startup (copies).</summary>
    public IReadOnlyList<CliServerPluginState> States()
    {
        lock (_lock) return _states.Select(s => s.Clone()).ToList();
    }

    /// <summary>A plugin to register: from the installed list (<see cref="Record"/>) or added in code.</summary>
    private sealed record Candidate(CliServerPluginRecord? Record, CliServerPluginState State, ICliPluginEntry Entry, int Order, int Index,
        string Name, string Package, string Version, string? Title, string Source, JsonObject? Options);

    /// <summary>A plugin added in code (<c>cli.AddPlugin</c>), run with the installed ones.</summary>
    private sealed record CodePlugin(ICliPluginEntry Entry, string? Id, JsonObject Options);

    private readonly List<CodePlugin> _code = [];

    /// <summary>Queues a plugin added in code; <see cref="Load"/> runs it with the installed ones, in the declared order.</summary>
    internal void AddCode(ICliPluginEntry entry, string? id, JsonObject options)
    {
        lock (_lock)
        {
            if (_loaded) throw new InvalidOperationException("plugins are added while the server's services are configured");
            _code.Add(new CodePlugin(entry, id, options));
        }
    }

    /// <summary>
    /// Registers the plugins added in code and the installed ones on <paramref name="builder"/>, in their declared order
    /// (once; <c>AddCli</c> calls it after the host's own registrations). Errors are kept, never thrown.
    /// </summary>
    public void Load(CliBuilder builder)
    {
        List<CodePlugin> code;
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            code = [.. _code];
        }
        var candidates = new List<Candidate>();
        var index = 0;
        foreach (var plugin in code)
        {
            var type = plugin.Entry.GetType();
            var assembly = type.Assembly;
            var attribute = assembly.GetCustomAttribute<CliPluginAttribute>() is { } a && a.EntryType == type ? a : null;
            var package = assembly.GetName().Name ?? type.Name;
            var version = (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString() ?? "0.0.0").Split('+')[0];
            var name = plugin.Id ?? attribute?.Id ?? package;
            var state = new CliServerPluginState
            {
                Name = name, Package = package, Version = version, Status = CliServerPluginStatuses.Loaded, Source = CliServerPluginSources.Code,
            };
            lock (_lock) _states.Add(state);
            candidates.Add(new Candidate(null, state, plugin.Entry, attribute?.Order ?? CliPluginOrder.Default, index++, name, package, version,
                null, CliServerPluginSources.Code, plugin.Options));
        }
        var records = Enabled ? Records() : [];
        foreach (var record in records)
        {
            var state = new CliServerPluginState { Name = record.Name, Package = record.Package, Version = record.Version, Status = CliServerPluginStatuses.Loaded };
            lock (_lock) _states.Add(state);
            try
            {
                var (entry, order) = Prepare(builder, record);
                candidates.Add(new Candidate(record, state, entry, order, index++, record.Name, record.Package, record.Version, record.Title,
                    CliServerPluginSources.Marketplace, null));
            }
            catch (Exception ex)
            {
                Fail(state, ex);
            }
        }
        foreach (var candidate in candidates.OrderBy(c => c.Order).ThenBy(c => c.Index))
        {
            try
            {
                Register(builder, candidate);
            }
            catch (Exception ex)
            {
                Fail(candidate.State, ex is TargetInvocationException { InnerException: { } inner } ? inner : ex);
            }
        }
        Publish();
    }

    private static void Fail(CliServerPluginState state, Exception ex)
    {
        state.Status = ex is SkippedException ? CliServerPluginStatuses.Skipped : CliServerPluginStatuses.Failed;
        var message = (ex.Message ?? ex.GetType().Name).Split('\n')[0];
        state.Error = message.Length > 500 ? message[..500] : message;
    }

    /// <summary>Loads a plugin's assembly in a context of its own and creates its entry. Throws why it cannot.</summary>
    private (ICliPluginEntry Entry, int Order) Prepare(CliBuilder builder, CliServerPluginRecord record)
    {
        if (builder.InstalledPlugins.Any(p => string.Equals(p.Id, record.Package, StringComparison.OrdinalIgnoreCase) && p.Source != CliInstalledPlugin.MarketplaceSource)
            || CliPluginLoadContext.HostHas(record.Package))
            throw new SkippedException("already installed by the host");
        if (States().Count(s => string.Equals(s.Package, record.Package, StringComparison.OrdinalIgnoreCase)) > 1)
            throw new SkippedException("listed twice");
        var dir = VersionDir(record.Package, record.Version);
        var main = Path.Combine(dir, record.Package + ".dll");
        if (!File.Exists(main)) throw new FileNotFoundException($"not installed in {dir}");
        var context = new CliPluginLoadContext(record.Name, dir);
        var assembly = context.LoadFromAssemblyPath(main);
        var attribute = assembly.GetCustomAttribute<CliPluginAttribute>()
            ?? throw new InvalidOperationException("the assembly declares no [assembly: CliPlugin(typeof(...))] entry");
        if (!typeof(ICliPluginEntry).IsAssignableFrom(attribute.EntryType))
            throw new InvalidOperationException($"{attribute.EntryType.FullName} does not implement ICliPluginEntry");
        var entry = (ICliPluginEntry)(Activator.CreateInstance(attribute.EntryType)
            ?? throw new InvalidOperationException($"could not create {attribute.EntryType.FullName}"));
        return (entry, attribute.Order);
    }

    /// <summary>The settings with the code options as their defaults (source <c>code</c>).</summary>
    internal static List<CliConfigSectionDefinition> WithOptions(List<CliConfigSectionDefinition> sections, JsonObject? options)
    {
        if (options == null || options.Count == 0) return sections;
        return sections.Select(section => new CliConfigSectionDefinition
        {
            Id = section.Id,
            Title = section.Title,
            Description = section.Description,
            AllowCustomKeys = section.AllowCustomKeys,
            Settings = section.Settings.Select(setting =>
            {
                // { key: value } is the section "settings"; { section: { key: value } } any section
                JsonNode? value = null;
                var found = options[section.Id] is JsonObject nested && nested.TryGetPropertyValue(setting.Key, out value)
                    || (section.Id == "settings" && options.TryGetPropertyValue(setting.Key, out value) && value is not JsonObject);
                if (!found || value == null) return setting;
                return new CliConfigSettingDefinition
                {
                    Key = setting.Key, Label = setting.Label, Description = setting.Description, Type = setting.Type,
                    Default = value.DeepClone(), Source = CliConfigSources.Code, Mutable = setting.Mutable,
                    RequiresRestart = setting.RequiresRestart, Sensitive = setting.Sensitive, Required = setting.Required,
                    Validation = setting.Validation, Editor = setting.Editor, Value = setting.Value, Validate = setting.Validate, Apply = setting.Apply,
                };
            }).ToList(),
        }).ToList();
    }

    private void Register(CliBuilder builder, Candidate candidate)
    {
        var services = builder.Services.Count;
        var snapshot = builder.Snapshot();
        if (candidate.Source == CliServerPluginSources.Marketplace)
        {
            // Listed first, as installed from the marketplace (the plugin's own listing of its assembly is then ignored)
            var record = candidate.Record!;
            builder.AddInstalledPlugin(new CliInstalledPlugin
            {
                Id = record.Package,
                Name = record.Title ?? record.Name,
                Version = record.Version,
                Description = record.Description,
                Icon = record.Icon,
                Source = CliInstalledPlugin.MarketplaceSource,
            });
        }
        else
        {
            builder.AddInstalledPlugin(candidate.Entry.GetType().Assembly);
        }
        var dataDir = Path.Combine(DataDir, "plugin-data", candidate.Name);
        Directory.CreateDirectory(dataDir);
        var logger = new CliPluginLogger($"Qodalis.Cli.Plugin:{candidate.Name}");
        var providers = new List<CliStoredConfigProvider>();
        var context = new CliPluginContext(candidate.Name, candidate.Package, candidate.Version, builder, logger, dataDir, DataDir, Restart, options =>
        {
            var sections = options.Sections ?? [new CliConfigSectionDefinition { Id = "settings", Title = "Settings", Settings = options.Settings ?? [] }];
            var provider = new CliStoredConfigProvider(new CliStoredConfigProviderOptions
            {
                Id = options.Id ?? candidate.Name,
                Title = options.Title ?? candidate.Title ?? candidate.Name,
                Description = options.Description,
                Store = options.Store,
                Sections = WithOptions(sections, candidate.Options),
            });
            builder.AddConfigProvider(provider);
            providers.Add(provider);
            return provider;
        }, candidate.Source, candidate.Options);
        try
        {
            candidate.Entry.Register(context);
        }
        catch
        {
            // Nothing of a failed plugin stays registered
            while (builder.Services.Count > services) builder.Services.RemoveAt(builder.Services.Count - 1);
            builder.Restore(snapshot);
            throw;
        }
        var listed = builder.InstalledPlugins.FirstOrDefault(p => string.Equals(p.Id, candidate.Package, StringComparison.OrdinalIgnoreCase));
        if (listed != null && candidate.Source == CliServerPluginSources.Code) listed.Source = CliInstalledPlugin.CodeSource;
        lock (_lock)
        {
            _loggers.Add(logger);
            foreach (var provider in providers)
            {
                _settings.Add((candidate.State, provider));
                if (provider.OwnStore == null) _ownStoreless.Add(provider.Id);
            }
            candidate.State.Settings = providers.FirstOrDefault()?.Id;
            if (listed != null) _installed[candidate.State] = listed;
        }
    }

    /// <summary>
    /// The states on the installed plugins' list (status, missing, settings) and in <c>installed.json</c> (with the
    /// error of a plugin that could not load, cleared once it loads), for the dashboard.
    /// </summary>
    private void Publish()
    {
        var states = States();
        lock (_lock)
        {
            foreach (var (state, plugin) in _installed)
            {
                plugin.Status = state.Status;
                plugin.Missing = state.Missing?.ToList();
                plugin.Settings = state.Settings;
            }
        }
        if (!Enabled) return;
        try
        {
            var records = Records();
            var changed = false;
            foreach (var record in records)
            {
                var state = states.FirstOrDefault(s => s.Source == CliServerPluginSources.Marketplace && s.Name == record.Name && s.Version == record.Version);
                if (state == null) continue;
                var error = state.Status is CliServerPluginStatuses.Failed or CliServerPluginStatuses.Skipped ? state.Error : null;
                if (record.Error == error && record.Status == state.Status && record.Settings == state.Settings
                    && (record.Missing ?? []).SequenceEqual(state.Missing ?? [])) continue;
                record.Error = error;
                record.Status = state.Status;
                record.Missing = state.Missing?.ToList();
                record.Settings = state.Settings;
                changed = true;
            }
            if (changed) Save(records);
        }
        catch (Exception)
        {
            // read-only directory: the states stay in this process
        }
    }

    /// <summary>
    /// Once the server started: the plugins' logs go to the server's logging, the restart service is set, and a loaded
    /// plugin whose required settings have no value is marked <c>needs-configuration</c>.
    /// </summary>
    internal Task StartedAsync(IServiceProvider services)
    {
        if (services.GetService<ILoggerFactory>() is { } factory)
        {
            List<CliPluginLogger> loggers;
            lock (_lock) loggers = [.. _loggers];
            foreach (var logger in loggers) logger.Attach(factory);
            var log = factory.CreateLogger("Qodalis.Cli.Plugins");
            foreach (var state in States())
            {
                if (state.Status == CliServerPluginStatuses.Loaded) log.LogInformation("Loaded {Name} ({Package} {Version})", state.Name, state.Package, state.Version);
                else log.LogWarning("{Name} ({Package} {Version}) not loaded: {Error}", state.Name, state.Package, state.Version, state.Error);
            }
        }
        Restart.Target = services.GetService<ICliServerRestart>();
        // The settings are read once the config stores are attached and loaded (after every hosted service started)
        if (services.GetService<IHostApplicationLifetime>() is { } lifetime)
        {
            lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
            {
                try
                {
                    if (services.GetService<CliConfigRegistry>() is { } registry) await registry.ReadyAsync();
                }
                catch (Exception)
                {
                    // logged by the config startup
                }
                await CheckSettingsAsync();
            }));
        }
        return Task.CompletedTask;
    }

    /// <summary>Marks the loaded plugins whose required settings have no value <c>needs-configuration</c> with <c>missing</c> (and back).</summary>
    public async Task CheckSettingsAsync()
    {
        List<(CliServerPluginState State, CliStoredConfigProvider Provider)> settings;
        lock (_lock) settings = [.. _settings];
        foreach (var group in settings.GroupBy(s => s.State))
        {
            var missing = new List<string>();
            foreach (var (_, provider) in group)
            {
                try
                {
                    await provider.ReadyAsync();
                    missing.AddRange(provider.Missing().Select(m => $"{provider.Id}.{m}"));
                }
                catch (Exception)
                {
                    // a store that cannot be read: the config page shows why
                }
            }
            lock (_lock)
            {
                if (group.Key.Status is not (CliServerPluginStatuses.Loaded or CliServerPluginStatuses.NeedsConfiguration)) continue;
                group.Key.Status = missing.Count > 0 ? CliServerPluginStatuses.NeedsConfiguration : CliServerPluginStatuses.Loaded;
                group.Key.Error = missing.Count > 0 ? $"needs configuration: {string.Join(", ", missing)}" : null;
                group.Key.Missing = missing.Count > 0 ? missing : null;
            }
        }
        Publish();
    }

    private sealed class SkippedException(string message) : Exception(message);
}

/// <summary>Hands the runtime-installed plugins the server's services once it started.</summary>
internal sealed class CliServerPluginsStartup(CliServerPlugins plugins, IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => plugins.StartedAsync(services);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
