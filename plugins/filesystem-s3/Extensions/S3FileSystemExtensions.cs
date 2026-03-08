namespace Qodalis.Cli.FileSystem.S3;

public static class S3FileSystemExtensions
{
    public static void UseS3(this FileSystemOptions options, S3ProviderOptions s3Options)
    {
        options.Provider = new S3FileStorageProvider(s3Options);
    }

    public static void UseS3(this FileSystemOptions options, Action<S3ProviderOptions> configure)
    {
        var s3Options = new S3ProviderOptions();
        configure(s3Options);
        options.Provider = new S3FileStorageProvider(s3Options);
    }
}
