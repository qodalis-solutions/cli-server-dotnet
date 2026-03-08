namespace Qodalis.Cli.FileSystem;

public static class FileSystemBuilderExtensions
{
    public static void UseInMemory(this FileSystemOptions options)
    {
        options.Provider = new InMemoryFileStorageProvider();
    }

    public static void UseOsFileSystem(this FileSystemOptions options, Action<OsProviderOptions>? configure = null)
    {
        var osOptions = new OsProviderOptions();
        configure?.Invoke(osOptions);
        options.Provider = new OsFileStorageProvider(osOptions);
    }
}
