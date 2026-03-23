using Amazon.S3;
using Amazon.S3.Model;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Aws.Services;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Aws.Processors;

/// <summary>
/// Utility methods for S3 URI parsing and byte formatting.
/// </summary>
internal static class S3Helpers
{
    /// <summary>
    /// Parses an S3 URI (e.g., "s3://bucket/key") into its bucket and key components.
    /// </summary>
    /// <param name="uri">The S3 URI to parse.</param>
    /// <returns>A tuple of (Bucket, Key) if valid, or <c>null</c> if the URI format is invalid.</returns>
    public static (string Bucket, string Key)? ParseS3Uri(string uri)
    {
        var match = System.Text.RegularExpressions.Regex.Match(uri, @"^s3://([^/]+)/?(.*)$");
        if (!match.Success) return null;
        return (match.Groups[1].Value, match.Groups[2].Value);
    }

    /// <summary>
    /// Formats a byte count into a human-readable string (e.g., "1.5 MB").
    /// </summary>
    /// <param name="bytes">The number of bytes.</param>
    /// <returns>A formatted string with the appropriate unit.</returns>
    public static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = (int)Math.Floor(Math.Log(bytes) / Math.Log(1024));
        return $"{bytes / Math.Pow(1024, i):F1} {units[i]}";
    }
}

/// <summary>
/// Handles the "s3 ls" command to list S3 buckets or objects within a bucket.
/// </summary>
internal class S3LsProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "ls";

    /// <inheritdoc />
    public override string Description { get; set; } = "List S3 buckets or objects in a bucket";

    /// <inheritdoc />
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "region",
            Aliases = ["-r"],
            Description = "AWS region override",
            Type = CommandParameterType.String,
        },
    ];

    private readonly AwsCredentialManager _credentialManager;

    /// <summary>
    /// Initializes a new instance of <see cref="S3LsProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the S3 client.</param>
    public S3LsProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonS3Client>(regionOverride);

        var value = command.Value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            try
            {
                var response = await client.ListBucketsAsync(ct);
                var buckets = response.Buckets ?? [];

                if (buckets.Count == 0)
                {
                    builder.WriteText("No buckets found.", "warning");
                    return builder.Build();
                }

                var items = buckets.Select(b =>
                {
                    var date = b.CreationDate != default ? b.CreationDate.ToString("yyyy-MM-dd") : "(unknown)";
                    return $"{date}  {b.BucketName}";
                }).ToArray();

                builder.WriteList(items);
            }
            catch (Exception ex)
            {
                builder.WriteText($"Failed to list buckets: {ex.Message}", "error");
                builder.SetExitCode(1);
            }

            return builder.Build();
        }

        var parsed = S3Helpers.ParseS3Uri(value);
        if (parsed == null)
        {
            builder.WriteText($"Invalid S3 URI: \"{value}\". Expected format: s3://bucket[/prefix]", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        try
        {
            var request = new ListObjectsV2Request
            {
                BucketName = parsed.Value.Bucket,
                Prefix = string.IsNullOrEmpty(parsed.Value.Key) ? null : parsed.Value.Key,
            };

            var objects = new List<S3Object>();
            ListObjectsV2Response listResponse;
            do
            {
                listResponse = await client.ListObjectsV2Async(request, ct);
                objects.AddRange(listResponse.S3Objects ?? []);
                request.ContinuationToken = listResponse.NextContinuationToken;
            } while (listResponse.IsTruncated);

            if (objects.Count == 0)
            {
                builder.WriteText($"No objects found in s3://{parsed.Value.Bucket}/{parsed.Value.Key}", "warning");
                return builder.Build();
            }

            var headers = new[] { "Last Modified", "Size", "Key" };
            var rows = objects.Select(obj => new[]
            {
                obj.LastModified != default ? obj.LastModified.ToString("yyyy-MM-dd HH:mm:ss") : "(unknown)",
                S3Helpers.FormatBytes(obj.Size),
                obj.Key ?? "",
            }).ToArray();

            builder.WriteTable(headers, rows);
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to list objects: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "s3 cp" command to copy objects between S3 locations (S3-to-S3 only).
/// </summary>
internal class S3CpProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "cp";

    /// <inheritdoc />
    public override string Description { get; set; } = "Copy objects between S3 locations (S3-to-S3 only)";

    /// <inheritdoc />
    public override bool? ValueRequired { get; set; } = true;

    /// <inheritdoc />
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "dest",
            Aliases = ["-d"],
            Description = "Destination S3 URI (s3://bucket/key)",
            Type = CommandParameterType.String,
            Required = true,
        },
        new CliCommandParameterDescriptor
        {
            Name = "region",
            Aliases = ["-r"],
            Description = "AWS region override",
            Type = CommandParameterType.String,
        },
    ];

    private readonly AwsCredentialManager _credentialManager;

    /// <summary>
    /// Initializes a new instance of <see cref="S3CpProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the S3 client.</param>
    public S3CpProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var source = command.Value?.Trim();
        var dest = command.Args.TryGetValue("dest", out var d) ? d.ToString() : null;

        if (string.IsNullOrEmpty(source))
        {
            builder.WriteText("Source S3 URI is required. Usage: s3 cp <s3://bucket/key> --dest <s3://bucket/key>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        if (string.IsNullOrEmpty(dest))
        {
            builder.WriteText("Destination is required. Use --dest <s3://bucket/key>.", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var srcParsed = S3Helpers.ParseS3Uri(source);
        if (srcParsed == null || string.IsNullOrEmpty(srcParsed.Value.Key))
        {
            builder.WriteText($"Invalid source S3 URI: \"{source}\". Expected format: s3://bucket/key", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var dstParsed = S3Helpers.ParseS3Uri(dest);
        if (dstParsed == null || string.IsNullOrEmpty(dstParsed.Value.Key))
        {
            builder.WriteText($"Invalid destination S3 URI: \"{dest}\". Expected format: s3://bucket/key", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonS3Client>(regionOverride);

        try
        {
            await client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = srcParsed.Value.Bucket,
                SourceKey = srcParsed.Value.Key,
                DestinationBucket = dstParsed.Value.Bucket,
                DestinationKey = dstParsed.Value.Key,
            }, ct);

            builder.WriteText($"Copied {source} to {dest}", "success");
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to copy object: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "s3 rm" command to delete an S3 object, with optional dry-run support.
/// </summary>
internal class S3RmProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "rm";

    /// <inheritdoc />
    public override string Description { get; set; } = "Delete an S3 object";

    /// <inheritdoc />
    public override bool? ValueRequired { get; set; } = true;

    /// <inheritdoc />
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "dry-run",
            Description = "Preview without deleting",
            Type = CommandParameterType.Boolean,
        },
        new CliCommandParameterDescriptor
        {
            Name = "region",
            Aliases = ["-r"],
            Description = "AWS region override",
            Type = CommandParameterType.String,
        },
    ];

    private readonly AwsCredentialManager _credentialManager;

    /// <summary>
    /// Initializes a new instance of <see cref="S3RmProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the S3 client.</param>
    public S3RmProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var value = command.Value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            builder.WriteText("S3 URI is required. Usage: s3 rm <s3://bucket/key>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var parsed = S3Helpers.ParseS3Uri(value);
        if (parsed == null || string.IsNullOrEmpty(parsed.Value.Key))
        {
            builder.WriteText($"Invalid S3 URI: \"{value}\". Expected format: s3://bucket/key", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        if (command.Args.ContainsKey("dry-run"))
        {
            builder.WriteText($"[DRY RUN] Would delete {value}", "warning");
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonS3Client>(regionOverride);

        try
        {
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = parsed.Value.Bucket,
                Key = parsed.Value.Key,
            }, ct);

            builder.WriteText($"Deleted {value}", "success");
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to delete object: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "s3 mb" command to create a new S3 bucket.
/// </summary>
internal class S3MbProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "mb";

    /// <inheritdoc />
    public override string Description { get; set; } = "Create an S3 bucket";

    /// <inheritdoc />
    public override bool? ValueRequired { get; set; } = true;

    /// <inheritdoc />
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "region",
            Aliases = ["-r"],
            Description = "AWS region override",
            Type = CommandParameterType.String,
        },
    ];

    private readonly AwsCredentialManager _credentialManager;

    /// <summary>
    /// Initializes a new instance of <see cref="S3MbProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the S3 client.</param>
    public S3MbProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var bucketName = command.Value?.Trim();

        if (string.IsNullOrEmpty(bucketName))
        {
            builder.WriteText("Bucket name is required. Usage: s3 mb <bucket-name>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonS3Client>(regionOverride);

        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucketName }, ct);
            builder.WriteText($"Bucket \"{bucketName}\" created successfully.", "success");
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to create bucket: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "s3 rb" command to delete an S3 bucket, with optional dry-run support.
/// </summary>
internal class S3RbProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "rb";

    /// <inheritdoc />
    public override string Description { get; set; } = "Delete an S3 bucket";

    /// <inheritdoc />
    public override bool? ValueRequired { get; set; } = true;

    /// <inheritdoc />
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "dry-run",
            Description = "Preview without deleting",
            Type = CommandParameterType.Boolean,
        },
        new CliCommandParameterDescriptor
        {
            Name = "region",
            Aliases = ["-r"],
            Description = "AWS region override",
            Type = CommandParameterType.String,
        },
    ];

    private readonly AwsCredentialManager _credentialManager;

    /// <summary>
    /// Initializes a new instance of <see cref="S3RbProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the S3 client.</param>
    public S3RbProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var bucketName = command.Value?.Trim();

        if (string.IsNullOrEmpty(bucketName))
        {
            builder.WriteText("Bucket name is required. Usage: s3 rb <bucket-name>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        if (command.Args.ContainsKey("dry-run"))
        {
            builder.WriteText($"[DRY RUN] Would delete bucket \"{bucketName}\"", "warning");
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonS3Client>(regionOverride);

        try
        {
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucketName }, ct);
            builder.WriteText($"Bucket \"{bucketName}\" deleted successfully.", "success");
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to delete bucket: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "s3 presign" command to generate a time-limited pre-signed URL for an S3 object.
/// </summary>
internal class S3PresignProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "presign";

    /// <inheritdoc />
    public override string Description { get; set; } = "Generate a pre-signed URL for an S3 object";

    /// <inheritdoc />
    public override bool? ValueRequired { get; set; } = true;

    /// <inheritdoc />
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "expires",
            Aliases = ["-e"],
            Description = "URL expiration in seconds (default: 3600)",
            Type = CommandParameterType.Number,
            DefaultValue = "3600",
        },
        new CliCommandParameterDescriptor
        {
            Name = "region",
            Aliases = ["-r"],
            Description = "AWS region override",
            Type = CommandParameterType.String,
        },
    ];

    private readonly AwsCredentialManager _credentialManager;

    /// <summary>
    /// Initializes a new instance of <see cref="S3PresignProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the S3 client.</param>
    public S3PresignProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var value = command.Value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            builder.WriteText("S3 URI is required. Usage: s3 presign <s3://bucket/key>", "error");
            builder.SetExitCode(1);
            return Task.FromResult<ICliStructuredResponse?>(builder.Build());
        }

        var parsed = S3Helpers.ParseS3Uri(value);
        if (parsed == null || string.IsNullOrEmpty(parsed.Value.Key))
        {
            builder.WriteText($"Invalid S3 URI: \"{value}\". Expected format: s3://bucket/key", "error");
            builder.SetExitCode(1);
            return Task.FromResult<ICliStructuredResponse?>(builder.Build());
        }

        var expiresIn = 3600;
        if (command.Args.TryGetValue("expires", out var e) && int.TryParse(e.ToString(), out var parsed2))
        {
            expiresIn = parsed2;
        }

        if (expiresIn <= 0)
        {
            builder.WriteText("--expires must be a positive number of seconds.", "error");
            builder.SetExitCode(1);
            return Task.FromResult<ICliStructuredResponse?>(builder.Build());
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonS3Client>(regionOverride);

        try
        {
            var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = parsed.Value.Bucket,
                Key = parsed.Value.Key,
                Expires = DateTime.UtcNow.AddSeconds(expiresIn),
            });

            builder.WriteText(url);
            builder.WriteText($"Expires in {expiresIn} seconds.", "muted");
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to generate pre-signed URL: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return Task.FromResult<ICliStructuredResponse?>(builder.Build());
    }
}

/// <summary>
/// Parent processor for S3 commands, aggregating ls, cp, rm, mb, rb, and presign sub-commands.
/// </summary>
public class S3Processor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "s3";

    /// <inheritdoc />
    public override string Description { get; set; } = "Amazon S3 operations — list, copy, remove objects and buckets";

    /// <inheritdoc />
    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    /// <summary>
    /// Initializes a new instance of <see cref="S3Processor"/> with its sub-command processors.
    /// </summary>
    /// <param name="credentialManager">The credential manager passed to child processors.</param>
    public S3Processor(AwsCredentialManager credentialManager)
    {
        Processors =
        [
            new S3LsProcessor(credentialManager),
            new S3CpProcessor(credentialManager),
            new S3RmProcessor(credentialManager),
            new S3MbProcessor(credentialManager),
            new S3RbProcessor(credentialManager),
            new S3PresignProcessor(credentialManager),
        ];
    }

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
