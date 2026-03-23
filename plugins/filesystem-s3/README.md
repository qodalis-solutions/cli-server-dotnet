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

## License

MIT
