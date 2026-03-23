namespace Qodalis.Cli.Plugin.FileSystem.S3;

/// <summary>
/// Extension methods for configuring the S3 file storage provider on <see cref="FileSystemOptions"/>.
/// </summary>
public static class S3FileSystemExtensions
{
    /// <summary>
    /// Configures the file system to use Amazon S3 as the storage backend.
    /// </summary>
    /// <param name="options">The file system options to configure.</param>
    /// <param name="s3Options">The S3 provider options specifying bucket, region, and credentials.</param>
    public static void UseS3(this FileSystemOptions options, S3ProviderOptions s3Options)
    {
        options.Provider = new S3FileStorageProvider(s3Options);
    }

    /// <summary>
    /// Configures the file system to use Amazon S3 as the storage backend with a configuration callback.
    /// </summary>
    /// <param name="options">The file system options to configure.</param>
    /// <param name="configure">A callback to configure the S3 provider options.</param>
    public static void UseS3(this FileSystemOptions options, Action<S3ProviderOptions> configure)
    {
        var s3Options = new S3ProviderOptions();
        configure(s3Options);
        options.Provider = new S3FileStorageProvider(s3Options);
    }
}
