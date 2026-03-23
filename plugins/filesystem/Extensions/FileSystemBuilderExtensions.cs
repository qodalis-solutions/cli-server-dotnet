namespace Qodalis.Cli.Plugin.FileSystem;

/// <summary>
/// Extension methods for configuring built-in file storage providers on <see cref="FileSystemOptions"/>.
/// </summary>
public static class FileSystemBuilderExtensions
{
    /// <summary>
    /// Configures the file system to use the in-memory storage provider.
    /// </summary>
    /// <param name="options">The file system options to configure.</param>
    public static void UseInMemory(this FileSystemOptions options)
    {
        options.Provider = new InMemoryFileStorageProvider();
    }

    /// <summary>
    /// Configures the file system to use the OS filesystem storage provider.
    /// </summary>
    /// <param name="options">The file system options to configure.</param>
    /// <param name="configure">Optional callback to configure OS provider options such as allowed paths.</param>
    public static void UseOsFileSystem(this FileSystemOptions options, Action<OsProviderOptions>? configure = null)
    {
        var osOptions = new OsProviderOptions();
        configure?.Invoke(osOptions);
        options.Provider = new OsFileStorageProvider(osOptions);
    }
}
