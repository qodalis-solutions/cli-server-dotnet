using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.FileSystem.Json.JsonFilePluginEntry), Id = "filesystem-json")]

namespace Qodalis.Cli.Plugin.FileSystem.Json;

/// <summary>
/// The JSON file storage from the dashboard (or <c>cli.AddPlugin&lt;JsonFilePluginEntry&gt;(new { file = "files.json", readOnly = false })</c>):
/// the filesystem API over a virtual tree kept in one JSON file (default
/// <c>&lt;data dir&gt;/plugin-data/filesystem-json/files.json</c>), read-only unless <c>readOnly</c> is turned off.
/// Refused when the host or another plugin already set the filesystem storage.
/// </summary>
public sealed class JsonFilePluginEntry : ICliPluginEntry
{
    /// <summary>Its settings (the marketplace's <c>config</c>; code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings =>
    [
        new()
        {
            Key = "file", Label = "JSON file", Description = "The file the tree is kept in (relative paths from the plugin's data directory)",
            Type = CliConfigEntryTypes.String, Default = "files.json", RequiresRestart = true, Validation = new CliConfigValidation { Required = true, Max = 500 },
        },
        FileSystemPluginEntry.ReadOnlySetting("Uploads, new folders and deletes are refused"),
    ];

    public void Register(CliPluginContext context) =>
        context.AddFileStorage(new CliPluginSettingsOptions
        {
            Title = "Filesystem (JSON file)", Description = "A virtual file tree kept in one JSON file", Settings = [.. Settings],
        }, "json", settings =>
        {
            var file = settings.Text("file") is { Length: > 0 } f ? f : "files.json";
            return new JsonFileStorageProvider(new JsonFileStorageProviderOptions { FilePath = Path.IsPathRooted(file) ? file : Path.Combine(context.DataDir, file) });
        });
}
