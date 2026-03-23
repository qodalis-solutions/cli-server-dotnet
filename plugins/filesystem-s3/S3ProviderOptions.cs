namespace Qodalis.Cli.Plugin.FileSystem.S3;

/// <summary>
/// Configuration options for the Amazon S3 file storage provider.
/// </summary>
public class S3ProviderOptions
{
    /// <summary>
    /// Gets or sets the S3 bucket name.
    /// </summary>
    public string Bucket { get; set; } = "";

    /// <summary>
    /// Gets or sets the AWS region (e.g., "us-east-1").
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Gets or sets an optional key prefix to scope all operations within the bucket.
    /// </summary>
    public string Prefix { get; set; } = "";

    /// <summary>
    /// Gets or sets a custom S3-compatible service URL (e.g., for MinIO or LocalStack).
    /// When set, path-style addressing is automatically enabled.
    /// </summary>
    public string? ServiceURL { get; set; }

    /// <summary>
    /// Gets or sets the AWS access key ID. If null, the default AWS credential chain is used.
    /// </summary>
    public string? AccessKeyId { get; set; }

    /// <summary>
    /// Gets or sets the AWS secret access key. If null, the default AWS credential chain is used.
    /// </summary>
    public string? SecretAccessKey { get; set; }
}
