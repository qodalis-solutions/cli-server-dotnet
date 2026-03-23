namespace Qodalis.Cli.Plugin.FileSystem.Json;

/// <summary>
/// Configuration options for the JSON file-based storage provider.
/// </summary>
public class JsonFileStorageProviderOptions
{
    /// <summary>
    /// Gets or sets the path to the JSON file used for persisting the virtual filesystem.
    /// </summary>
    public string FilePath { get; set; } = "./data/files.json";
}
