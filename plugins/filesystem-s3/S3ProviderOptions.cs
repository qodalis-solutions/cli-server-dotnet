namespace Qodalis.Cli.Plugin.FileSystem.S3;

public class S3ProviderOptions
{
    public string Bucket { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public string Prefix { get; set; } = "";
    public string? ServiceURL { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
}
