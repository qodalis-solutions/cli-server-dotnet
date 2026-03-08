namespace Qodalis.Cli.FileSystem.Json;

public static class JsonFileSystemExtensions
{
    public static void UseJsonFile(this FileSystemOptions options, string filePath)
    {
        options.Provider = new JsonFileStorageProvider(new JsonFileStorageProviderOptions { FilePath = filePath });
    }
}
