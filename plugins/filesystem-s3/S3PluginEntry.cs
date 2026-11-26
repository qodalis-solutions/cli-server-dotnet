using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.FileSystem.S3.S3PluginEntry), Id = "filesystem-s3")]

namespace Qodalis.Cli.Plugin.FileSystem.S3;

/// <summary>
/// The S3 file storage from the dashboard (or <c>cli.AddPlugin&lt;S3PluginEntry&gt;(new { bucket = "files" })</c>): the
/// filesystem API over an S3 (or S3-compatible) bucket. The bucket (required), region, prefix and endpoint are settings,
/// the access keys secrets (never returned; without them the AWS default credential chain is used). Read-only unless
/// <c>readOnly</c> is turned off; nothing is served until a bucket is set. Refused when the storage is already set.
/// </summary>
public sealed class S3PluginEntry : ICliPluginEntry
{
    /// <summary>Its settings (the marketplace's <c>config</c>; code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings =>
    [
        new() { Key = "bucket", Label = "Bucket", Type = CliConfigEntryTypes.String, Required = true, Default = "", RequiresRestart = true, Validation = new CliConfigValidation { Max = 63 } },
        new() { Key = "region", Label = "Region", Type = CliConfigEntryTypes.String, Default = "us-east-1", RequiresRestart = true },
        new() { Key = "prefix", Label = "Key prefix", Description = "Prepended to every path", Type = CliConfigEntryTypes.String, Default = "", RequiresRestart = true },
        new() { Key = "endpoint", Label = "Endpoint", Description = "An S3-compatible service (MinIO); empty: AWS", Type = CliConfigEntryTypes.String, Default = "", RequiresRestart = true },
        new() { Key = "accessKeyId", Label = "Access key id", Description = "Empty: the AWS default credential chain", Type = CliConfigEntryTypes.Secret, RequiresRestart = true },
        new() { Key = "secretAccessKey", Label = "Secret access key", Type = CliConfigEntryTypes.Secret, RequiresRestart = true },
        FileSystemPluginEntry.ReadOnlySetting("Uploads, new folders and deletes are refused"),
    ];

    public void Register(CliPluginContext context) =>
        context.AddFileStorage(new CliPluginSettingsOptions
        {
            Title = "Filesystem (S3)", Description = "An S3 or S3-compatible bucket served by the filesystem API", Settings = [.. Settings],
        }, "s3", settings =>
        {
            var key = settings.Text("accessKeyId");
            var secret = settings.Text("secretAccessKey");
            return new S3FileStorageProvider(new S3ProviderOptions
            {
                Bucket = settings.Text("bucket"),
                Region = settings.Text("region") is { Length: > 0 } region ? region : "us-east-1",
                Prefix = settings.Text("prefix"),
                ServiceURL = settings.Text("endpoint") is { Length: > 0 } endpoint ? endpoint : null,
                AccessKeyId = key.Length > 0 && secret.Length > 0 ? key : null,
                SecretAccessKey = key.Length > 0 && secret.Length > 0 ? secret : null,
            });
        });
}
