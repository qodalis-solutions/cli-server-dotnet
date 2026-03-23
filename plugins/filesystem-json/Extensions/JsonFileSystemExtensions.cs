namespace Qodalis.Cli.Plugin.FileSystem.Json;

/// <summary>
/// Extension methods for configuring the JSON file storage provider on <see cref="FileSystemOptions"/>.
/// </summary>
public static class JsonFileSystemExtensions
{
    /// <summary>
    /// Configures the file system to use a JSON file as the storage backend.
    /// </summary>
    /// <param name="options">The file system options to configure.</param>
    /// <param name="filePath">The path to the JSON file used for persistence.</param>
    public static void UseJsonFile(this FileSystemOptions options, string filePath)
    {
        options.Provider = new JsonFileStorageProvider(new JsonFileStorageProviderOptions { FilePath = filePath });
    }
}
