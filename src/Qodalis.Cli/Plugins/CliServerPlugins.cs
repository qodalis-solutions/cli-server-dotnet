using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
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
    /// <summary>The server's data directory unless set: <c>QCLI_DATA_DIR</c>, else <c>qcli-data</c> in the working directory.</summary>
    public const string DefaultDataDir = "qcli-data";

    /// <summary>The environment variable that moves the data directory when <see cref="DataDir"/> is not set.</summary>
    public const string DataDirEnvironmentVariable = "QCLI_DATA_DIR";

    /// <summary>The server's data directory: <paramref name="dataDir"/> when set, else <c>QCLI_DATA_DIR</c>, else <c>qcli-data</c> in the working directory (absolute).</summary>
    public static string ResolveDataDir(string? dataDir = null) =>
        ResolveDataDir(dataDir, Environment.GetEnvironmentVariable(DataDirEnvironmentVariable));

    /// <summary>The server's data directory from <paramref name="dataDir"/>, else <paramref name="environmentValue"/> (the value of <c>QCLI_DATA_DIR</c>), else <c>qcli-data</c> in the working directory (absolute).</summary>
    public static string ResolveDataDir(string? dataDir, string? environmentValue) =>
        Path.GetFullPath(!string.IsNullOrEmpty(dataDir) ? dataDir
            : !string.IsNullOrEmpty(environmentValue) ? environmentValue
            : Path.Combine(Directory.GetCurrentDirectory(), DefaultDataDir));

    /// <summary>False: installed plugins are not loaded, and the server marketplace cannot install any.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The server's data directory (default: the <c>QCLI_DATA_DIR</c> environment variable, else
    /// <c>&lt;working directory&gt;/qcli-data</c>): runtime-installed server plugins, their data and settings, and the
    /// admin plugin's dashboard plugins. It must be writable.
    /// </summary>
    public string? DataDir { get; set; }

    /// <summary>The plugins directory (default <c>&lt;data dir&gt;/server-plugins</c>).</summary>
    public string? Dir { get; set; }
}

/// <summary>
/// Loads a plugin's assemblies in a collectible context of its own; an assembly the host has (<c>Qodalis.Cli</c>, its
/// abstractions, the framework, any package the host references) resolves to the host's copy, so there is one copy of the
/// contracts and of every shared type. The plugin's folder holds its <c>lib</c> assemblies, satellite assemblies in
/// <c>&lt;culture&gt;/</c>, and the assets of this runtime and its fallbacks in <c>runtimes/&lt;rid&gt;/lib/&lt;tfm&gt;/</c>
/// (preferred over the <c>lib</c> ones) and <c>runtimes/&lt;rid&gt;/native/</c> (native libraries).
/// </summary>
public sealed class CliPluginLoadContext(string name, string dir) : AssemblyLoadContext($"qcli-plugin:{name}", isCollectible: true)
{
    private static readonly Lazy<Dictionary<string, string>> HostAssemblies = new(() =>
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                paths.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        return paths;
    });

    private static readonly Lazy<IReadOnlyList<string>> CurrentRids = new(() => RuntimeIdentifierChain(RuntimeInformation.RuntimeIdentifier));

    /// <summary>True when the host has the assembly (its dependencies, or loaded already).</summary>
    public static bool HostHas(string assemblyName) =>
        HostAssemblies.Value.ContainsKey(assemblyName)
        || Default.Assemblies.Any(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The version of the host's copy of an assembly: its informational version (the package version, without
    /// <c>+metadata</c>), else its file version, else its assembly version; null when the host does not have it.
    /// </summary>
    public static string? HostVersion(string assemblyName)
    {
        var loaded = Default.Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        if (loaded != null)
        {
            var informational = loaded.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? loaded.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            return Plain(informational) ?? loaded.GetName().Version?.ToString();
        }
        if (!HostAssemblies.Value.TryGetValue(assemblyName, out var path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            if (!pe.HasMetadata) return null;
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            string? informational = null, file = null;
            foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = md.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != System.Reflection.Metadata.HandleKind.MemberReference) continue;
                var parent = md.GetMemberReference((System.Reflection.Metadata.MemberReferenceHandle)attribute.Constructor).Parent;
                if (parent.Kind != System.Reflection.Metadata.HandleKind.TypeReference) continue;
                var typeName = md.GetString(md.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)parent).Name);
                if (typeName is not ("AssemblyInformationalVersionAttribute" or "AssemblyFileVersionAttribute")) continue;
                var blob = md.GetBlobReader(attribute.Value);
                if (blob.Length < 2 || blob.ReadUInt16() != 1) continue;
                var value = blob.ReadSerializedString();
                if (typeName == "AssemblyInformationalVersionAttribute") informational = value;
                else file = value;
            }
            return Plain(informational) ?? Plain(file) ?? md.GetAssemblyDefinition().Version.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }

        static string? Plain(string? version) => string.IsNullOrWhiteSpace(version) ? null : version.Split('+')[0].Trim();
    }

    /// <summary>
    /// A runtime identifier and the ones it falls back to, most specific first, as NuGet's portable graph has them:
    /// <c>osx-arm64</c>: osx-arm64, osx, unix, any; <c>linux-musl-x64</c>: linux-musl-x64, linux-musl, linux-x64, linux, unix,
    /// any; <c>win-x64</c>: win-x64, win, any.
    /// </summary>
    public static IReadOnlyList<string> RuntimeIdentifierChain(string rid)
    {
        var chain = new List<string>();
        void Add(string r)
        {
            if (!chain.Contains(r, StringComparer.OrdinalIgnoreCase)) chain.Add(r);
        }
        rid = rid.Trim().ToLowerInvariant();
        Add(rid);
        var dash = rid.LastIndexOf('-');
        var os = dash > 0 ? rid[..dash] : rid;
        var arch = dash > 0 ? rid[(dash + 1)..] : null;
        // A versioned RID (osx.14-arm64, ubuntu.22.04-x64) falls back to its family
        var family = os.Split('.')[0];
        if (family != os && arch != null) Add($"{family}-{arch}");
        if (family == "linux-musl")
        {
            Add("linux-musl");
            if (arch != null) Add($"linux-{arch}");
            Add("linux");
        }
        else if (family is "ubuntu" or "debian" or "rhel" or "fedora" or "centos" or "alpine" or "opensuse" or "sles")
        {
            if (arch != null) Add($"linux-{arch}");
            Add("linux");
        }
        else
        {
            Add(family);
        }
        if (!family.StartsWith("win", StringComparison.Ordinal)) Add("unix");
        Add("any");
        return chain;
    }

    /// <summary>This runtime's identifier and its fallbacks (<see cref="RuntimeIdentifierChain"/>).</summary>
    public static IReadOnlyList<string> RuntimeIdentifiers => CurrentRids.Value;

    public string Dir { get; } = dir;

    /// <summary>
    /// The assembly <paramref name="name"/> as code of <paramref name="owner"/> sees it: the copy in the load context
    /// that loaded <paramref name="owner"/> (a plugin's own, else the host's), loaded there when it is not yet. Never
    /// another plugin's copy, as <c>AppDomain.GetAssemblies()</c> may give (its types would not match the caller's).
    /// Null when it cannot be found.
    /// </summary>
    public static Assembly? FindAssembly(string name, Assembly owner) =>
        FindAssembly(name, GetLoadContext(owner) ?? Default);

    /// <summary>The assembly <paramref name="name"/> in <paramref name="context"/>, loaded there when it is not yet; null when not found.</summary>
    public static Assembly? FindAssembly(string name, AssemblyLoadContext context)
    {
        var loaded = context.Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        if (loaded != null) return loaded;
        try
        {
            return context.LoadFromAssemblyName(new AssemblyName(name));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The file of a managed assembly in the plugin's folder: a satellite in <c>&lt;culture&gt;/</c>, else this runtime's
    /// (<c>runtimes/&lt;rid&gt;/lib/&lt;tfm&gt;/</c>, the most specific RID first), else the <c>lib</c> one; null when the
    /// host has it or the folder does not.
    /// </summary>
    internal string? ResolveManaged(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name == null || name.IndexOfAny(['/', '\\']) >= 0 || name.Contains("..", StringComparison.Ordinal)) return null;
        if (!string.IsNullOrEmpty(assemblyName.CultureName))
        {
            var culture = assemblyName.CultureName;
            if (culture.IndexOfAny(['/', '\\', '.']) >= 0) return null;
            var satellite = Path.Combine(Dir, culture, name + ".dll");
            return File.Exists(satellite) ? satellite : null;
        }
        if (HostHas(name)) return null;
        foreach (var rid in RuntimeIdentifiers)
        {
            var ridLib = Path.Combine(Dir, "runtimes", rid, "lib");
            if (!Directory.Exists(ridLib)) continue;
            foreach (var tfm in Directory.EnumerateDirectories(ridLib))
            {
                var candidate = Path.Combine(tfm, name + ".dll");
                if (File.Exists(candidate)) return candidate;
            }
        }
        var path = Path.Combine(Dir, name + ".dll");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The file of a native library in the plugin's folder: <c>runtimes/&lt;rid&gt;/native/</c> for this runtime and its
    /// fallbacks, then the folder itself, trying the platform's names (<c>e_sqlite3</c>: <c>libe_sqlite3.dylib</c>,
    /// <c>libe_sqlite3.so</c>, <c>e_sqlite3.dll</c>); null when none is there.
    /// </summary>
    internal string? ResolveUnmanaged(string unmanagedDllName)
    {
        if (string.IsNullOrEmpty(unmanagedDllName) || unmanagedDllName.IndexOfAny(['/', '\\', ':']) >= 0 || unmanagedDllName.Contains("..", StringComparison.Ordinal))
            return null;
        var dirs = RuntimeIdentifiers.Select(rid => Path.Combine(Dir, "runtimes", rid, "native")).Append(Dir);
        foreach (var folder in dirs)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var candidate in NativeNames(unmanagedDllName))
            {
                var path = Path.Combine(folder, candidate);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    /// <summary>The names a native library may have on this platform.</summary>
    internal static IEnumerable<string> NativeNames(string name)
    {
        yield return name;
        if (OperatingSystem.IsWindows())
        {
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) yield return name + ".dll";
            yield break;
        }
        var extension = OperatingSystem.IsMacOS() ? ".dylib" : ".so";
        var hasPrefix = name.StartsWith("lib", StringComparison.Ordinal);
        if (!name.EndsWith(extension, StringComparison.Ordinal))
        {
            if (!hasPrefix) yield return "lib" + name + extension;
            yield return name + extension;
        }
        if (!hasPrefix) yield return "lib" + name;
    }

    protected override Assembly? Load(AssemblyName assemblyName) =>
        ResolveManaged(assemblyName) is { } path ? LoadFromAssemblyPath(path) : null;

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
        ResolveUnmanaged(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
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
    private readonly Dictionary<CliServerPluginState, List<string>> _needs = new();
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
        DataDir = CliServerPluginsOptions.ResolveDataDir(options.DataDir);
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

    /// <summary>
    /// The files of an installed plugin's folder: each path relative to <paramref name="dir"/> (<c>/</c>-separated) with
    /// <c>sha256-&lt;base64&gt;</c> of its bytes (<see cref="CliServerPluginRecord.Files"/>).
    /// </summary>
    public static Dictionary<string, string> FileManifest(string dir)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            files[Path.GetRelativePath(dir, path).Replace(Path.DirectorySeparatorChar, '/')] = Sha256Of(path);
        return new Dictionary<string, string>(files, StringComparer.Ordinal);
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return $"sha256-{Convert.ToBase64String(SHA256.HashData(stream))}";
    }

    /// <summary>
    /// True when <paramref name="dir"/> holds exactly the files of <paramref name="manifest"/> with their hashes (none
    /// missing, none added, none changed); false without a manifest.
    /// </summary>
    public static bool FilesUnchanged(string dir, IReadOnlyDictionary<string, string>? manifest)
    {
        if (manifest == null || manifest.Count == 0 || !Directory.Exists(dir)) return false;
        var actual = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dir, p).Replace(Path.DirectorySeparatorChar, '/')).ToList();
        if (actual.Count != manifest.Count) return false;
        foreach (var relative in actual)
        {
            if (!manifest.TryGetValue(relative, out var expected)) return false;
            var hash = Sha256Of(Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(expected))) return false;
        }
        return true;
    }

    private readonly List<string> _removed = [];

    /// <summary>
    /// Deletes the version folders <c>installed.json</c> no longer lists (a plugin updated or removed while it was
    /// loaded: its folder could not be deleted then), the leftovers of interrupted installs, and package folders left
    /// empty. Runs at startup before any plugin is loaded; nothing is deleted when the list cannot be read. Returns the
    /// folders deleted (relative to <see cref="Dir"/>).
    /// </summary>
    internal IReadOnlyList<string> RemoveStaleVersions()
    {
        var file = Path.Combine(Dir, InstalledFile);
        if (!Directory.Exists(Dir) || !File.Exists(file)) return [];
        var keep = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file))?["plugins"] is not JsonArray plugins) return [];
            foreach (var node in plugins)
            {
                // Every listed package and version is kept, even a record the loader leaves out
                if (node?["package"] is JsonValue p && p.TryGetValue<string>(out var package)
                    && node["version"] is JsonValue v && v.TryGetValue<string>(out var version))
                    keep.Add($"{package}/{version}");
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
        var removed = new List<string>();
        foreach (var packageDir in Directory.EnumerateDirectories(Dir))
        {
            var package = Path.GetFileName(packageDir);
            if (!IsValidPackage(package)) continue;
            foreach (var versionDir in Directory.EnumerateDirectories(packageDir))
            {
                var version = Path.GetFileName(versionDir);
                if (keep.Contains($"{package}/{version}")) continue;
                try
                {
                    Directory.Delete(versionDir, recursive: true);
                    removed.Add($"{package}/{version}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // tried again at the next start
                }
            }
            try
            {
                if (!Directory.EnumerateFileSystemEntries(packageDir).Any()) Directory.Delete(packageDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        lock (_lock) _removed.AddRange(removed);
        return removed;
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
            var attribute = type.GetCustomAttribute<CliPluginAttribute>()
                ?? (assembly.GetCustomAttribute<CliPluginAttribute>() is { } a && a.EntryType == type ? a : null);
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
        // Before anything is loaded: the folders of versions updated or removed while the last run had them loaded
        if (Enabled) RemoveStaleVersions();
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

    /// <summary>
    /// The entry of the marketplace plugin <paramref name="name"/> in the host's own copy of its package: the server's
    /// entries (<c>filesystem</c>, <c>data-explorer</c>: their packages are part of the server on .NET), else the
    /// <c>[assembly: CliPlugin]</c> of <paramref name="package"/> when the host references it (as a dependency of
    /// another package, without adding it). Installing the plugin from the marketplace then turns that copy on (one
    /// copy of its types: the host's). Null when the host has no entry for it.
    /// </summary>
    public static CliPluginAttribute? HostEntry(string? name, string? package)
    {
        if (name == null || package == null || !CliPluginLoadContext.HostHas(package)) return null;
        if (BuiltInEntries.Value.GetValueOrDefault(name) is { } builtIn) return builtIn;
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(package));
            return assembly.GetCustomAttribute<CliPluginAttribute>() is { } entry
                   && (entry.Id == null || string.Equals(entry.Id, name, StringComparison.OrdinalIgnoreCase))
                   && typeof(ICliPluginEntry).IsAssignableFrom(entry.EntryType)
                ? entry
                : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    private static readonly Lazy<Dictionary<string, CliPluginAttribute>> BuiltInEntries = new(() =>
        typeof(CliServerPlugins).Assembly.GetTypes()
            .Select(t => t.GetCustomAttribute<CliPluginAttribute>())
            .OfType<CliPluginAttribute>()
            .Where(a => a.Id != null && typeof(ICliPluginEntry).IsAssignableFrom(a.EntryType))
            .ToDictionary(a => a.Id!, StringComparer.OrdinalIgnoreCase));

    /// <summary>Loads a plugin's assembly in a context of its own and creates its entry. Throws why it cannot.</summary>
    private (ICliPluginEntry Entry, int Order) Prepare(CliBuilder builder, CliServerPluginRecord record)
    {
        if (builder.InstalledPlugins.Any(p => string.Equals(p.Id, record.Package, StringComparison.OrdinalIgnoreCase) && p.Source != CliInstalledPlugin.MarketplaceSource))
            throw new SkippedException("already installed by the host");
        if (States().Count(s => string.Equals(s.Package, record.Package, StringComparison.OrdinalIgnoreCase)) > 1)
            throw new SkippedException("listed twice");
        if (CliPluginLoadContext.HostHas(record.Package))
        {
            // A package the host carries without adding it (Qodalis.Cli.Plugin.FileSystem, part of the server; a
            // dependency of another package): its entry in the host's copy, nothing loaded from the plugin's folder
            if (HostEntry(record.Name, record.Package) is { } builtIn)
                return ((ICliPluginEntry)(Activator.CreateInstance(builtIn.EntryType)
                    ?? throw new InvalidOperationException($"could not create {builtIn.EntryType.FullName}")), builtIn.Order);
            throw new SkippedException("already installed by the host");
        }
        var dir = VersionDir(record.Package, record.Version);
        var main = Path.Combine(dir, record.Package + ".dll");
        if (!File.Exists(main)) throw new FileNotFoundException($"not installed in {dir}");
        // Nothing is loaded from a folder that differs from what was verified and extracted at install
        if (!FilesUnchanged(dir, record.Files)) throw new InvalidOperationException("files changed since install");
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
        }, candidate.Source, candidate.Options, PluginSettingsStore);
        try
        {
            candidate.Entry.Register(context);
        }
        catch
        {
            // Nothing of a failed plugin stays registered but its settings, so what made it fail (a connection, a
            // dialect whose provider is missing) can be corrected from the dashboard before the next restart
            while (builder.Services.Count > services) builder.Services.RemoveAt(builder.Services.Count - 1);
            builder.Restore(snapshot);
            foreach (var provider in providers) builder.AddConfigProvider(provider);
            lock (_lock)
            {
                foreach (var provider in providers)
                {
                    _settings.Add((candidate.State, provider));
                    if (provider.OwnStore == null) _ownStoreless.Add(provider.Id);
                }
                candidate.State.Settings = providers.FirstOrDefault()?.Id;
            }
            throw;
        }
        // An entry of the server itself (the filesystem's) is not a package of its own
        var listed = candidate.Entry.GetType().Assembly == typeof(CliBuilder).Assembly ? null
            : builder.InstalledPlugins.FirstOrDefault(p => string.Equals(p.Id, candidate.Package, StringComparison.OrdinalIgnoreCase));
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
            if (context.Needs.Count > 0)
            {
                _needs[candidate.State] = [.. context.Needs];
                candidate.State.Status = CliServerPluginStatuses.NeedsConfiguration;
                candidate.State.Missing = [.. context.Needs];
                candidate.State.Error = $"needs configuration: {string.Join(", ", context.Needs)}";
            }
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
            List<string> removed;
            lock (_lock) removed = [.. _removed];
            foreach (var folder in removed) log.LogInformation("Removed {Folder} (no longer installed)", folder);
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
                try
                {
                    await CheckSettingsAsync();
                }
                finally
                {
                    _startupCheck.TrySetResult();
                }
            }));
        }
        return Task.CompletedTask;
    }

    private readonly TaskCompletionSource _startupCheck = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the settings check run after the server started has finished (and published the states).</summary>
    public Task StartupCheck => _startupCheck.Task;

    /// <summary>Marks the loaded plugins whose required settings have no value <c>needs-configuration</c> with <c>missing</c> (and back).</summary>
    public async Task CheckSettingsAsync()
    {
        List<(CliServerPluginState State, CliStoredConfigProvider Provider)> settings;
        Dictionary<CliServerPluginState, List<string>> needs;
        lock (_lock)
        {
            settings = [.. _settings];
            needs = new(_needs);
        }
        var states = settings.Select(s => s.State).Concat(needs.Keys).Distinct().ToList();
        foreach (var state in states)
        {
            var missing = new List<string>(needs.GetValueOrDefault(state) ?? []);
            foreach (var (_, provider) in settings.Where(s => s.State == state))
            {
                try
                {
                    await provider.ReadyAsync();
                    missing.AddRange(provider.Missing());
                }
                catch (Exception)
                {
                    // a store that cannot be read: the config page shows why
                }
            }
            lock (_lock)
            {
                if (state.Status is not (CliServerPluginStatuses.Loaded or CliServerPluginStatuses.NeedsConfiguration)) continue;
                state.Status = missing.Count > 0 ? CliServerPluginStatuses.NeedsConfiguration : CliServerPluginStatuses.Loaded;
                state.Error = missing.Count > 0 ? $"needs configuration: {string.Join(", ", missing)}" : null;
                state.Missing = missing.Count > 0 ? missing : null;
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
