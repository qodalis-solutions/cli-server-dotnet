# Qodalis.Cli.Plugin.FileSystem.S3

Amazon S3 storage provider for [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet) file storage. Stores files and directories in an S3 bucket, with support for S3-compatible services like MinIO and LocalStack.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.FileSystem.S3
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Plugin.FileSystem.S3;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddFileSystem(o => o.UseS3(s3 =>
    {
        s3.Bucket = "my-bucket";
        s3.Region = "us-east-1";
    }));
});
```

## Configuration

| Option | Type | Default | Description |
|---|---|---|---|
| `Bucket` | `string` | `""` | S3 bucket name |
| `Region` | `string` | `us-east-1` | AWS region |
| `Prefix` | `string` | `""` | Key prefix to scope operations within the bucket |
| `ServiceURL` | `string?` | `null` | Custom S3-compatible endpoint (e.g., MinIO, LocalStack) |
| `AccessKeyId` | `string?` | `null` | AWS access key (uses default credential chain if null) |
| `SecretAccessKey` | `string?` | `null` | AWS secret key (uses default credential chain if null) |

When `ServiceURL` is set, path-style addressing is automatically enabled for compatibility with S3-compatible services.

## Dependencies

- `Qodalis.Cli.Plugin.FileSystem`
- `AWSSDK.S3`

## Terminal commands

None of its own: the storage is reached through the [filesystem plugin](../filesystem/README.md#terminal-commands)
(`scp` in the web CLI).

## Install from the dashboard or in code

The package declares an entry point (`S3PluginEntry`, plugin id `filesystem-s3`), so it can be installed from the dashboard's
marketplace (the admin plugin's server plugins, loaded at the next start) or added in code through the same entry:

```csharp
cli.AddPlugin<S3PluginEntry>(new { bucket = "files" });
```

Its settings (config provider `filesystem-s3` unless noted; code options are their defaults, saved settings override them):
`bucket` (required), `region`, `prefix`, `endpoint`, `accessKeyId` and `secretAccessKey` (secrets), `readOnly` (default true); nothing is served until a bucket is set. A required setting without a value lists the plugin as `needs-configuration` with `missing`; see
[Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard).

## License

MIT
