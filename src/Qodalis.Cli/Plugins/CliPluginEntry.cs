using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.Restart;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugins;

// Server plugins installed at runtime (from the marketplace): the entry point a plugin package declares and the context
// the server hands it. The same contract on every platform: Node (qodalis.entry exporting createPlugin(context)),
// Python (entry point group qodalis_cli.plugins) and .NET:
//
//     [assembly: CliPlugin(typeof(WeatherPluginEntry))]
//
//     public sealed class WeatherPluginEntry : ICliPluginEntry
//     {
//         public void Register(CliPluginContext context)
//         {
//             var settings = context.Settings(new() { Title = "Weather", Settings = [new() { Key = "defaultLocation", Type = "string", Default = "London" }] });
//             context.Builder.AddModule(new WeatherModule(() => settings.GetValue("settings", "defaultLocation")?.ToString()));
//         }
//     }

/// <summary>When a plugin registers among the others (ascending; then in the installed order).</summary>
public static class CliPluginOrder
{
    /// <summary>Authentication (users and its stores): first, the others may need the principal.</summary>
    public const int Auth = 10;

    /// <summary>Config stores: before the providers whose settings they keep.</summary>
    public const int ConfigStore = 20;

    /// <summary>Core services other plugins extend (data explorer, filesystem storage).</summary>
    public const int Core = 30;

    /// <summary>Most plugins.</summary>
    public const int Default = 50;

    /// <summary>What plugs into another plugin (data explorer sources).</summary>
    public const int Source = 70;
}

/// <summary>
/// Declares an assembly's entry point for installs from the marketplace: <c>[assembly: CliPlugin(typeof(MyPluginEntry))]</c>,
/// the type implementing <see cref="ICliPluginEntry"/> with a public parameterless constructor.
/// </summary>
// Also on an entry class (its id and order when added in code), for an assembly with several entries
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = false)]
public sealed class CliPluginAttribute(Type entryType) : Attribute
{
    public Type EntryType { get; } = entryType;

    /// <summary>
    /// The plugin's id (its marketplace entry, <c>weather</c>): the default id of its settings when added in code with
    /// <c>cli.AddPlugin</c> (installed from the marketplace, the marketplace's id is used). Default: the assembly's name.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>When it registers among the installed plugins (<see cref="CliPluginOrder"/>; default <see cref="CliPluginOrder.Default"/>).</summary>
    public int Order { get; set; } = CliPluginOrder.Default;
}

/// <summary>The entry point of a server plugin installed at runtime.</summary>
public interface ICliPluginEntry
{
    /// <summary>
    /// Registers the plugin as the host would in code (<c>context.Builder.AddModule(...)</c>, services, config providers),
    /// while the server's services are being registered. Read settings lazily (their stored values are loaded once the
    /// server starts). Throwing leaves the plugin out (its services removed); the server starts all the same.
    /// </summary>
    void Register(CliPluginContext context);
}

/// <summary>The settings of a plugin: one <see cref="CliStoredConfigProvider"/>, registered with the server's config providers.</summary>
public sealed class CliPluginSettingsOptions
{
    /// <summary>The provider id (default: the plugin's id).</summary>
    public string? Id { get; init; }

    /// <summary>The provider's title (default: the plugin's title).</summary>
    public string? Title { get; init; }

    public string? Description { get; init; }

    /// <summary>One section <c>settings</c> with these settings.</summary>
    public List<CliConfigSettingDefinition>? Settings { get; init; }

    /// <summary>Several sections instead.</summary>
    public List<CliConfigSectionDefinition>? Sections { get; init; }

    /// <summary>Its own store (the host's store for the provider id still wins, else the server's default store).</summary>
    public ICliConfigStore? Store { get; init; }
}

/// <summary>What the server hands a plugin's <see cref="ICliPluginEntry.Register"/>.</summary>
public sealed class CliPluginContext
{
    private readonly Func<CliPluginSettingsOptions, CliStoredConfigProvider> _settings;
    private readonly ICliConfigStore? _settingsFile;

    internal CliPluginContext(string pluginId, string packageName, string version, CliBuilder builder, ILogger logger, string dataDir,
        string serverDataDir, ICliServerRestart restart, Func<CliPluginSettingsOptions, CliStoredConfigProvider> settings,
        string source = CliServerPluginSources.Marketplace, System.Text.Json.Nodes.JsonObject? options = null, ICliConfigStore? settingsFile = null)
    {
        _settingsFile = settingsFile;
        Source = source;
        Options = options ?? [];
        PluginId = pluginId;
        PackageName = packageName;
        Version = version;
        Builder = builder;
        Logger = logger;
        DataDir = dataDir;
        ServerDataDir = serverDataDir;
        Restart = restart;
        _settings = settings;
    }

    /// <summary>The marketplace entry's id (<c>weather</c>).</summary>
    public string PluginId { get; }

    /// <summary><c>marketplace</c> (installed at runtime) or <c>code</c> (<c>cli.AddPlugin</c>).</summary>
    public string Source { get; }

    /// <summary>
    /// The options of a plugin added in code (<c>cli.AddPlugin&lt;T&gt;(options)</c>): <c>{ key: value }</c> for the
    /// section <c>settings</c>, or <c>{ section: { key: value } }</c>; empty for one installed from the marketplace.
    /// <see cref="Settings"/> takes them as the defaults (source <c>code</c>); saved settings override them.
    /// </summary>
    public System.Text.Json.Nodes.JsonObject Options { get; }

    /// <summary>The NuGet package (<c>Qodalis.Cli.Plugin.Weather</c>).</summary>
    public string PackageName { get; }

    /// <summary>The installed version.</summary>
    public string Version { get; }

    /// <summary>The server's builder: everything is registered as if the host had done it in code.</summary>
    public CliBuilder Builder { get; }

    /// <summary>The server's services (<see cref="CliBuilder.Services"/>).</summary>
    public IServiceCollection Services => Builder.Services;

    /// <summary>Logs under the category <c>Qodalis.Cli.Plugin:&lt;id&gt;</c> (kept until the server's logging starts).</summary>
    public ILogger Logger { get; }

    /// <summary>A directory of the plugin's own (<c>&lt;data dir&gt;/plugin-data/&lt;id&gt;</c>), created; kept across updates.</summary>
    public string DataDir { get; }

    /// <summary>The server's data directory (<c>qcli-data</c> in the working directory by default).</summary>
    public string ServerDataDir { get; }

    /// <summary>The server's restart service (usable once the server started; a setting that needs a restart marks it).</summary>
    public ICliServerRestart Restart { get; }

    /// <summary>
    /// Registers the plugin's settings as a config provider (defaults, validation, values kept in the config store),
    /// edited from the dashboard's Config page and <c>config set &lt;id&gt;.&lt;section&gt;.&lt;key&gt;</c>. The code
    /// options (<see cref="Options"/>) are the defaults, with source <c>code</c>; saved settings override them. Read a
    /// value with <c>provider.GetValue(section, key)</c> once the server started (the stored values load then: read
    /// them lazily). A <c>Required</c> setting without a value marks the plugin <c>needs-configuration</c> (with
    /// <c>missing</c>); it should then stay idle until the value is set.
    /// </summary>
    public CliStoredConfigProvider Settings(CliPluginSettingsOptions options) => _settings(options ?? new CliPluginSettingsOptions());

    /// <summary>What the plugin still needs (<see cref="NeedsConfiguration"/>).</summary>
    internal List<string> Needs { get; } = [];

    /// <summary>
    /// Marks the plugin <c>needs-configuration</c> for a reason besides its own required settings (another plugin it
    /// builds on: <c>"users plugin"</c>); it should register nothing else and is loaded again at the next start.
    /// </summary>
    public void NeedsConfiguration(params string[] missing)
    {
        foreach (var m in missing) if (!string.IsNullOrWhiteSpace(m) && !Needs.Contains(m)) Needs.Add(m);
    }

    /// <summary>
    /// Settings needed while the services are registered (a config store's own connection, a database's): kept in
    /// <c>&lt;data dir&gt;/plugin-settings.json</c> (never in the store they may set up) and read now, so
    /// <c>provider.GetValue</c> already returns the saved values. A change applies after a restart.
    /// </summary>
    public CliStoredConfigProvider BootSettings(CliPluginSettingsOptions options)
    {
        var store = options?.Store ?? _settingsFile ?? throw new InvalidOperationException("no plugin settings file");
        var provider = _settings(new CliPluginSettingsOptions
        {
            Id = options?.Id, Title = options?.Title, Description = options?.Description, Settings = options?.Settings, Sections = options?.Sections, Store = store,
        });
        provider.AttachStoreAsync(store).GetAwaiter().GetResult();
        return provider;
    }
}

/// <summary>How a server plugin was added.</summary>
public static class CliServerPluginSources
{
    /// <summary>Installed at runtime from the marketplace (<c>installed.json</c>).</summary>
    public const string Marketplace = "marketplace";

    /// <summary>Added by the host in code (<c>cli.AddPlugin</c>).</summary>
    public const string Code = "code";
}

/// <summary>The statuses of a server plugin in this process.</summary>
public static class CliServerPluginStatuses
{
    public const string Loaded = "loaded";
    public const string Failed = "failed";
    public const string Skipped = "skipped";

    /// <summary>Loaded, but a required setting has no value: it waits for its settings.</summary>
    public const string NeedsConfiguration = "needs-configuration";
}

/// <summary>A plugin installed at runtime, as <c>&lt;data dir&gt;/server-plugins/installed.json</c> keeps it.</summary>
public sealed class CliServerPluginRecord
{
    /// <summary>The marketplace entry's id (<c>weather</c>).</summary>
    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>The NuGet package id.</summary>
    [JsonPropertyName("package")] public required string Package { get; init; }

    [JsonPropertyName("version")] public required string Version { get; init; }

    /// <summary><c>sha512-…</c> of the .nupkg, checked before it was installed.</summary>
    [JsonPropertyName("integrity")] public required string Integrity { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>
    /// Every file of its folder (paths relative to it, <c>/</c>-separated) with its <c>sha256-…</c>, written when it was
    /// extracted: at startup the folder must hold exactly these files with these hashes, or the plugin is not loaded
    /// (<c>failed</c>, "files changed since install").
    /// </summary>
    [JsonPropertyName("files")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Files { get; init; }

    /// <summary>ISO 8601 UTC.</summary>
    [JsonPropertyName("installedAt")] public required string InstalledAt { get; init; }

    [JsonPropertyName("installedBy")] public required string InstalledBy { get; init; }

    /// <summary>Why the last start could not load it (cleared once it loads).</summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    /// <summary>Its status at the last start (<see cref="CliServerPluginStatuses"/>), kept for the dashboard.</summary>
    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; set; }

    /// <summary>While <c>needs-configuration</c>: the required settings without a value.</summary>
    [JsonPropertyName("missing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Missing { get; set; }

    /// <summary>The config provider of its settings (<c>config set &lt;settings&gt;.settings.&lt;key&gt;</c>).</summary>
    [JsonPropertyName("settings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Settings { get; set; }
}

/// <summary>A plugin the server tried to load at startup.</summary>
public sealed class CliServerPluginState
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("package")] public required string Package { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }

    /// <summary><see cref="CliServerPluginStatuses"/>.</summary>
    [JsonPropertyName("status")] public required string Status { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    /// <summary><see cref="CliServerPluginSources"/>: <c>marketplace</c> or <c>code</c>.</summary>
    [JsonPropertyName("source")] public string Source { get; init; } = CliServerPluginSources.Marketplace;

    /// <summary>The required settings without a value (<c>&lt;section&gt;.&lt;key&gt;</c> of the <see cref="Settings"/> provider, as on the Node and Python servers), and the plugins it needs, while <c>needs-configuration</c>.</summary>
    [JsonPropertyName("missing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Missing { get; set; }

    /// <summary>The config provider of its settings, when it has some.</summary>
    [JsonPropertyName("settings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Settings { get; set; }

    internal CliServerPluginState Clone() => new()
    {
        Name = Name, Package = Package, Version = Version, Status = Status, Error = Error, Source = Source, Missing = Missing?.ToList(),
        Settings = Settings,
    };
}
