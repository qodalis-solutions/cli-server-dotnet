using System.Net;
using System.Text;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace Qodalis.Cli.FileSystem.S3;

public class S3FileStorageProvider : IFileStorageProvider, IDisposable
{
    public string Name => "s3";

    private readonly S3ProviderOptions _options;
    private readonly IAmazonS3 _client;

    public S3FileStorageProvider(S3ProviderOptions options)
        : this(options, CreateClient(options))
    {
    }

    public S3FileStorageProvider(S3ProviderOptions options, IAmazonS3 client)
    {
        _options = options;
        _client = client;
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    // --- IFileStorageProvider implementation ---

    public async Task<List<FileEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var prefix = ToKey(path);
        if (prefix.Length > 0 && !prefix.EndsWith('/'))
            prefix += "/";

        // If listing a non-root path, verify it exists as a directory
        if (path.Trim('/').Length > 0)
        {
            var dirExists = await IsDirectoryAsync(prefix, ct);
            if (!dirExists)
            {
                // Check if it exists as a file
                var fileExists = await ObjectExistsAsync(prefix.TrimEnd('/'), ct);
                if (fileExists)
                    throw new FileStorageNotADirectoryError(path);
                throw new FileStorageNotFoundError(path);
            }
        }

        var request = new ListObjectsV2Request
        {
            BucketName = _options.Bucket,
            Prefix = prefix,
            Delimiter = "/",
        };

        var entries = new List<FileEntry>();
        ListObjectsV2Response response;

        do
        {
            response = await _client.ListObjectsV2Async(request, ct);

            // Directories (common prefixes)
            foreach (var commonPrefix in response.CommonPrefixes)
            {
                var name = ExtractName(commonPrefix, prefix);
                if (string.IsNullOrEmpty(name))
                    continue;

                entries.Add(new FileEntry
                {
                    Name = name,
                    Type = "directory",
                    Size = 0,
                    Modified = "",
                });
            }

            // Files
            foreach (var obj in response.S3Objects)
            {
                var key = obj.Key;

                // Skip the directory marker itself
                if (key == prefix)
                    continue;

                var name = key[prefix.Length..];

                // Skip nested objects (shouldn't happen with delimiter, but be safe)
                if (name.Contains('/'))
                    continue;

                entries.Add(new FileEntry
                {
                    Name = name,
                    Type = "file",
                    Size = obj.Size,
                    Modified = obj.LastModified.ToString("o"),
                });
            }

            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated);

        return entries;
    }

    public async Task<string> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var key = ToKey(path);
        await EnsureIsFileAsync(path, key, ct);

        var response = await GetObjectSafeAsync(key, ct)
            ?? throw new FileStorageNotFoundError(path);

        using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    public Task WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        return WriteFileAsync(path, Encoding.UTF8.GetBytes(content), ct);
    }

    public async Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        var key = ToKey(path);
        var parentPath = GetParentPath(path);

        // Verify parent directory exists (for non-root parents)
        if (parentPath.Trim('/').Length > 0)
        {
            var parentKey = ToKey(parentPath);
            if (!parentKey.EndsWith('/'))
                parentKey += "/";

            var parentExists = await IsDirectoryAsync(parentKey, ct);
            if (!parentExists)
                throw new FileStorageNotFoundError(path);
        }

        // Check if the path is a directory
        if (await IsDirectoryAsync(key + "/", ct))
            throw new FileStorageIsADirectoryError(path);

        using var stream = new MemoryStream(content);
        var request = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = stream,
        };

        await _client.PutObjectAsync(request, ct);
    }

    public async Task<FileStat> StatAsync(string path, CancellationToken ct = default)
    {
        var key = ToKey(path);

        // Try as file first
        var obj = await GetMetadataSafeAsync(key, ct);
        if (obj != null)
        {
            // Could be a directory marker (trailing /)
            if (key.EndsWith('/'))
            {
                return new FileStat
                {
                    Name = GetName(path),
                    Type = "directory",
                    Size = 0,
                    Created = obj.LastModified.ToString("o"),
                    Modified = obj.LastModified.ToString("o"),
                };
            }

            return new FileStat
            {
                Name = GetName(path),
                Type = "file",
                Size = obj.ContentLength,
                Created = obj.LastModified.ToString("o"),
                Modified = obj.LastModified.ToString("o"),
            };
        }

        // Try as directory (prefix with /)
        var dirKey = key.EndsWith('/') ? key : key + "/";
        if (await IsDirectoryAsync(dirKey, ct))
        {
            return new FileStat
            {
                Name = GetName(path),
                Type = "directory",
                Size = 0,
                Created = "",
                Modified = "",
            };
        }

        throw new FileStorageNotFoundError(path);
    }

    public async Task MkdirAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath == "/")
            return;

        if (recursive)
        {
            var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var currentPath = "";
            foreach (var part in parts)
            {
                currentPath = currentPath.Length == 0 ? part : currentPath + "/" + part;
                var dirKey = ToKey(currentPath) + "/";

                // Check if something exists as a file at this path
                var fileKey = ToKey(currentPath);
                if (await ObjectExistsAsync(fileKey, ct))
                    throw new FileStorageNotADirectoryError(part);

                if (!await ObjectExistsAsync(dirKey, ct))
                    await PutDirectoryMarkerAsync(dirKey, ct);
            }
        }
        else
        {
            var parentPath = GetParentPath(normalizedPath);

            if (parentPath.Trim('/').Length > 0)
            {
                var parentKey = ToKey(parentPath) + "/";
                if (!await IsDirectoryAsync(parentKey, ct))
                    throw new FileStorageNotFoundError(path);
            }

            var key = ToKey(normalizedPath);

            // Check if already exists
            if (await ObjectExistsAsync(key + "/", ct) || await IsDirectoryAsync(key + "/", ct))
                throw new FileStorageExistsError(path);

            await PutDirectoryMarkerAsync(key + "/", ct);
        }
    }

    public async Task RemoveAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath == "/")
            throw new FileStoragePermissionError(path);

        var key = ToKey(normalizedPath);

        // Check if it's a file
        if (await ObjectExistsAsync(key, ct))
        {
            await _client.DeleteObjectAsync(_options.Bucket, key, ct);
            return;
        }

        // Check if it's a directory
        var dirKey = key + "/";
        if (!await IsDirectoryAsync(dirKey, ct))
            throw new FileStorageNotFoundError(path);

        if (!recursive)
        {
            // Check if directory has children (beyond the marker)
            var listRequest = new ListObjectsV2Request
            {
                BucketName = _options.Bucket,
                Prefix = dirKey,
                MaxKeys = 2,
            };
            var listResponse = await _client.ListObjectsV2Async(listRequest, ct);
            var childCount = listResponse.S3Objects.Count(o => o.Key != dirKey);
            var hasCommonPrefixes = listResponse.CommonPrefixes.Count > 0;

            if (childCount > 0 || hasCommonPrefixes)
                throw new FileStoragePermissionError(path);
        }

        // Delete all objects under the prefix
        var deleteRequest = new ListObjectsV2Request
        {
            BucketName = _options.Bucket,
            Prefix = dirKey,
        };

        ListObjectsV2Response deleteResponse;
        do
        {
            deleteResponse = await _client.ListObjectsV2Async(deleteRequest, ct);

            if (deleteResponse.S3Objects.Count > 0)
            {
                var deleteObjectsRequest = new DeleteObjectsRequest
                {
                    BucketName = _options.Bucket,
                    Objects = deleteResponse.S3Objects
                        .Select(o => new KeyVersion { Key = o.Key })
                        .ToList(),
                };
                await _client.DeleteObjectsAsync(deleteObjectsRequest, ct);
            }

            deleteRequest.ContinuationToken = deleteResponse.NextContinuationToken;
        } while (deleteResponse.IsTruncated);

        // Also delete the directory marker itself if it wasn't included
        await DeleteObjectSafeAsync(dirKey, ct);
    }

    public async Task CopyAsync(string src, string dest, CancellationToken ct = default)
    {
        var srcKey = ToKey(src);
        var destKey = ToKey(dest);

        if (!await ObjectExistsAsync(srcKey, ct))
            throw new FileStorageNotFoundError(src);

        var destParent = GetParentPath(dest);
        if (destParent.Trim('/').Length > 0)
        {
            var parentKey = ToKey(destParent) + "/";
            if (!await IsDirectoryAsync(parentKey, ct))
                throw new FileStorageNotFoundError(dest);
        }

        var copyRequest = new CopyObjectRequest
        {
            SourceBucket = _options.Bucket,
            SourceKey = srcKey,
            DestinationBucket = _options.Bucket,
            DestinationKey = destKey,
        };

        await _client.CopyObjectAsync(copyRequest, ct);
    }

    public async Task MoveAsync(string src, string dest, CancellationToken ct = default)
    {
        await CopyAsync(src, dest, ct);
        var srcKey = ToKey(src);
        await _client.DeleteObjectAsync(_options.Bucket, srcKey, ct);
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var key = ToKey(path);

        // Check as file
        if (await ObjectExistsAsync(key, ct))
            return true;

        // Check as directory
        return await IsDirectoryAsync(key + "/", ct);
    }

    public async Task<Stream> GetDownloadStreamAsync(string path, CancellationToken ct = default)
    {
        var key = ToKey(path);

        // Check if it's a directory
        if (key.EndsWith('/') || await IsDirectoryAsync(key + "/", ct))
        {
            if (!await ObjectExistsAsync(key, ct))
                throw new FileStorageIsADirectoryError(path);
        }

        var response = await GetObjectSafeAsync(key, ct)
            ?? throw new FileStorageNotFoundError(path);

        // Copy to memory stream so we can dispose the S3 response
        var ms = new MemoryStream();
        await response.ResponseStream.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    public Task UploadFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        return WriteFileAsync(path, content, ct);
    }

    // --- Helpers ---

    private string ToKey(string path)
    {
        var normalized = NormalizePath(path).TrimStart('/');
        if (_options.Prefix.Length > 0)
        {
            var prefix = _options.Prefix.TrimEnd('/');
            return normalized.Length > 0 ? $"{prefix}/{normalized}" : prefix + "/";
        }
        return normalized;
    }

    private static string NormalizePath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "/";
        return "/" + string.Join("/", parts);
    }

    private static string GetParentPath(string path)
    {
        var normalized = NormalizePath(path);
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash <= 0)
            return "/";
        return normalized[..lastSlash];
    }

    private static string GetName(string path)
    {
        var normalized = NormalizePath(path);
        var lastSlash = normalized.LastIndexOf('/');
        return normalized[(lastSlash + 1)..];
    }

    private static string ExtractName(string commonPrefix, string parentPrefix)
    {
        var relative = commonPrefix[parentPrefix.Length..];
        return relative.TrimEnd('/');
    }

    private async Task<bool> ObjectExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            await _client.GetObjectMetadataAsync(_options.Bucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task<bool> IsDirectoryAsync(string dirKey, CancellationToken ct)
    {
        // A directory exists if the marker object exists or if any objects share the prefix
        if (await ObjectExistsAsync(dirKey, ct))
            return true;

        var request = new ListObjectsV2Request
        {
            BucketName = _options.Bucket,
            Prefix = dirKey,
            MaxKeys = 1,
        };
        var response = await _client.ListObjectsV2Async(request, ct);
        return response.S3Objects.Count > 0 || response.CommonPrefixes.Count > 0;
    }

    private async Task PutDirectoryMarkerAsync(string key, CancellationToken ct)
    {
        var request = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = new MemoryStream([]),
            ContentType = "application/x-directory",
        };
        await _client.PutObjectAsync(request, ct);
    }

    private async Task<GetObjectResponse?> GetObjectSafeAsync(string key, CancellationToken ct)
    {
        try
        {
            return await _client.GetObjectAsync(_options.Bucket, key, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<GetObjectMetadataResponse?> GetMetadataSafeAsync(string key, CancellationToken ct)
    {
        try
        {
            return await _client.GetObjectMetadataAsync(_options.Bucket, key, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task DeleteObjectSafeAsync(string key, CancellationToken ct)
    {
        try
        {
            await _client.DeleteObjectAsync(_options.Bucket, key, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already deleted, ignore
        }
    }

    private async Task EnsureIsFileAsync(string path, string key, CancellationToken ct)
    {
        if (await IsDirectoryAsync(key + "/", ct))
            throw new FileStorageIsADirectoryError(path);
    }

    private static IAmazonS3 CreateClient(S3ProviderOptions options)
    {
        var config = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region),
        };

        if (!string.IsNullOrEmpty(options.ServiceURL))
        {
            config.ServiceURL = options.ServiceURL;
            config.ForcePathStyle = true;
        }

        if (!string.IsNullOrEmpty(options.AccessKeyId) && !string.IsNullOrEmpty(options.SecretAccessKey))
        {
            return new AmazonS3Client(options.AccessKeyId, options.SecretAccessKey, config);
        }

        return new AmazonS3Client(config);
    }
}
