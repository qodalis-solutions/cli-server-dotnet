using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Plugins;

namespace Qodalis.Cli.Plugins;

/// <summary>Helpers of the official plugins' entries.</summary>
public static class CliPluginContextExtensions
{
    /// <summary>A setting's text (trimmed; empty when unset).</summary>
    public static string Text(this CliStoredConfigProvider settings, string key, string section = "settings") =>
        settings.GetValue(section, key) is { } v ? (v is JsonValue j && j.TryGetValue<string>(out var s) ? s : v.ToJsonString()).Trim() : string.Empty;

    /// <summary>A boolean setting; <paramref name="fallback"/> when unset.</summary>
    public static bool Flag(this CliStoredConfigProvider settings, string key, bool fallback, string section = "settings") =>
        settings.GetValue(section, key) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    /// <summary>A number setting; <paramref name="fallback"/> when unset.</summary>
    public static double Number(this CliStoredConfigProvider settings, string key, double fallback, string section = "settings") =>
        double.TryParse(settings.GetValue(section, key)?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : fallback;

    /// <summary>A list setting (<c>string[]</c>); empty when unset.</summary>
    public static IReadOnlyList<string> List(this CliStoredConfigProvider settings, string key, string section = "settings") =>
        settings.GetValue(section, key) is JsonArray a
            ? a.Select(i => i is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null).OfType<string>().Where(s => s.Length > 0).ToList()
            : [];

    /// <summary>
    /// Sets the filesystem storage of a storage plugin from its settings: <paramref name="create"/> builds the storage
    /// from them on first use (null while a required one is missing: the API then answers that it needs
    /// configuration), read-only unless the setting <c>readOnly</c> is false. Throws when the host or another plugin
    /// already set the storage.
    /// </summary>
    public static CliStoredConfigProvider AddFileStorage(this CliPluginContext context, CliPluginSettingsOptions options, string name,
        Func<CliStoredConfigProvider, IFileStorageProvider?> create)
    {
        if (context.Builder.HasFileStorage) throw new InvalidOperationException("the filesystem storage is already set by the host or another plugin");
        var settings = context.Settings(options);
        var storage = new DeferredFileStorageProvider(name, () =>
        {
            if (settings.Missing().Count > 0) return null;
            var inner = create(settings);
            return inner == null ? null : settings.Flag("readOnly", true) ? new ReadOnlyFileStorageProvider(inner) : inner;
        });
        context.Builder.AddFileSystem(o => o.Provider = storage);
        return settings;
    }
}

/// <summary>
/// The filesystem plugin from the dashboard (or <c>cli.AddPlugin&lt;FileSystemPluginEntry&gt;(new { roots = new[] { "/srv/files" } })</c>):
/// the filesystem API, the <c>fs_*</c> MCP tools and the dashboard's Files page over folders of the server's disk. The
/// folders are a required setting (nothing is served until some are set), read-only unless <c>readOnly</c> is turned
/// off; read on first use (a restart applies a change). On .NET the package is part of the server: installing it from the
/// marketplace turns this entry on (nothing is downloaded), unless the host adds the filesystem in code.
/// </summary>
[CliPlugin(typeof(FileSystemPluginEntry), Id = "filesystem")]
public sealed class FileSystemPluginEntry : ICliPluginEntry
{
    /// <summary>Its settings (the marketplace's <c>config</c>; code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings =>
    [
        new()
        {
            Key = "roots", Label = "Folders", Description = "Absolute paths served (and everything below them); none: the filesystem API serves nothing",
            Type = CliConfigEntryTypes.StringList, Required = true, Default = new JsonArray(), RequiresRestart = true,
            Validation = new CliConfigValidation { Pattern = @"^(/|[A-Za-z]:[\\/]).*" },
        },
        ReadOnlySetting("Only list, read and download: uploads, new folders and deletes are refused"),
    ];

    /// <summary>The <c>readOnly</c> setting of the storage plugins (default true).</summary>
    public static CliConfigSettingDefinition ReadOnlySetting(string description) => new()
    {
        Key = "readOnly", Label = "Read-only", Description = description, Type = CliConfigEntryTypes.Boolean, Default = true, RequiresRestart = true,
    };

    public void Register(CliPluginContext context) =>
        // Not "filesystem": the server's own provider of that id describes the storage in use
        context.AddFileStorage(new CliPluginSettingsOptions
        {
            Id = "filesystem-os", Title = "Filesystem (server disk)", Description = "Folders of the server's disk served by the filesystem API",
            Settings = [.. Settings],
        }, "os", settings => new OsFileStorageProvider(new OsProviderOptions
        {
            AllowedPaths = [.. settings.List("roots")],
            // never the server's data directory (installed plugins, their manifests, the plugins' settings)
            DeniedPaths = [context.ServerDataDir],
        }));
}
