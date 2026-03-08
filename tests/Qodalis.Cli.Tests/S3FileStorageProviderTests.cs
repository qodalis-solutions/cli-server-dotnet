using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Moq;
using Qodalis.Cli.FileSystem;
using Qodalis.Cli.FileSystem.S3;

namespace Qodalis.Cli.Tests;

public class S3FileStorageProviderTests : IDisposable
{
    private readonly Mock<IAmazonS3> _mockS3;
    private readonly S3ProviderOptions _options;
    private readonly S3FileStorageProvider _provider;

    public S3FileStorageProviderTests()
    {
        _mockS3 = new Mock<IAmazonS3>();
        _options = new S3ProviderOptions
        {
            Bucket = "test-bucket",
            Region = "us-east-1",
            Prefix = "",
        };
        _provider = new S3FileStorageProvider(_options, _mockS3.Object);
    }

    public void Dispose()
    {
        _provider.Dispose();
    }

    // --- Name ---

    [Fact]
    public void Name_ReturnsS3()
    {
        Assert.Equal("s3", _provider.Name);
    }

    // --- ExistsAsync ---

    [Fact]
    public async Task ExistsAsync_ReturnsTrueForFile()
    {
        SetupObjectExists("file.txt", true);

        Assert.True(await _provider.ExistsAsync("file.txt"));
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueForDirectory()
    {
        SetupObjectExists("dir/", true);

        Assert.True(await _provider.ExistsAsync("dir"));
    }

    [Fact]
    public async Task ExistsAsync_ReturnsFalseForNonexistent()
    {
        SetupObjectExists("nope", false);
        SetupObjectExists("nope/", false);
        SetupEmptyListing("nope/");

        Assert.False(await _provider.ExistsAsync("nope"));
    }

    // --- ReadFileAsync ---

    [Fact]
    public async Task ReadFileAsync_ReturnsContent()
    {
        var content = "Hello, S3!";
        SetupObjectExists("docs/hello.txt/", false);
        SetupEmptyListing("docs/hello.txt/");

        SetupGetObject("docs/hello.txt", content);

        var result = await _provider.ReadFileAsync("docs/hello.txt");
        Assert.Equal(content, result);
    }

    [Fact]
    public async Task ReadFileAsync_ThrowsWhenNotFound()
    {
        SetupObjectExists("docs/hello.txt/", false);
        SetupEmptyListing("docs/hello.txt/");
        SetupGetObjectNotFound("docs/hello.txt");

        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.ReadFileAsync("docs/hello.txt"));
    }

    [Fact]
    public async Task ReadFileAsync_ThrowsWhenIsDirectory()
    {
        SetupObjectExists("dir/", true);

        await Assert.ThrowsAsync<FileStorageIsADirectoryError>(
            () => _provider.ReadFileAsync("dir"));
    }

    // --- WriteFileAsync ---

    [Fact]
    public async Task WriteFileAsync_PutsObject()
    {
        // Parent directory exists
        SetupObjectExists("docs/", true);

        // Not a directory
        SetupObjectExists("docs/file.txt/", false);
        SetupEmptyListing("docs/file.txt/");

        PutObjectRequest? capturedRequest = null;
        _mockS3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new PutObjectResponse());

        await _provider.WriteFileAsync("docs/file.txt", "hello");

        Assert.NotNull(capturedRequest);
        Assert.Equal("test-bucket", capturedRequest!.BucketName);
        Assert.Equal("docs/file.txt", capturedRequest.Key);
    }

    [Fact]
    public async Task WriteFileAsync_ThrowsWhenParentMissing()
    {
        SetupObjectExists("missing/", false);
        SetupEmptyListing("missing/");

        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.WriteFileAsync("missing/file.txt", "content"));
    }

    [Fact]
    public async Task WriteFileAsync_ThrowsWhenIsDirectory()
    {
        // Parent exists
        SetupObjectExists("docs/", true);

        // The target IS a directory
        SetupObjectExists("docs/subdir/", true);

        await Assert.ThrowsAsync<FileStorageIsADirectoryError>(
            () => _provider.WriteFileAsync("docs/subdir", "content"));
    }

    // --- WriteFileAsync (bytes) ---

    [Fact]
    public async Task WriteFileAsync_Bytes_PutsObject()
    {
        SetupObjectExists("bin/", true);
        SetupObjectExists("bin/data.bin/", false);
        SetupEmptyListing("bin/data.bin/");

        PutObjectRequest? capturedRequest = null;
        _mockS3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new PutObjectResponse());

        var data = new byte[] { 0x01, 0x02, 0x03 };
        await _provider.WriteFileAsync("bin/data.bin", data);

        Assert.NotNull(capturedRequest);
        Assert.Equal("bin/data.bin", capturedRequest!.Key);
    }

    // --- MkdirAsync ---

    [Fact]
    public async Task MkdirAsync_CreatesDirectoryMarker()
    {
        // No file at this path
        SetupObjectExists("newdir", false);
        // No existing directory marker
        SetupObjectExists("newdir/", false);
        SetupEmptyListing("newdir/");

        PutObjectRequest? capturedRequest = null;
        _mockS3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new PutObjectResponse());

        await _provider.MkdirAsync("newdir");

        Assert.NotNull(capturedRequest);
        Assert.Equal("newdir/", capturedRequest!.Key);
        Assert.Equal("application/x-directory", capturedRequest.ContentType);
    }

    [Fact]
    public async Task MkdirAsync_ThrowsWhenAlreadyExists()
    {
        SetupObjectExists("existing/", true);

        await Assert.ThrowsAsync<FileStorageExistsError>(
            () => _provider.MkdirAsync("existing"));
    }

    [Fact]
    public async Task MkdirAsync_NonRecursive_ThrowsWhenParentMissing()
    {
        SetupObjectExists("missing/", false);
        SetupEmptyListing("missing/");

        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.MkdirAsync("missing/child"));
    }

    [Fact]
    public async Task MkdirAsync_Recursive_CreatesIntermediateDirectories()
    {
        // None of the paths exist as files
        SetupObjectExists("a", false);
        SetupObjectExists("a/b", false);
        SetupObjectExists("a/b/c", false);

        // None of the directory markers exist
        SetupObjectExists("a/", false);
        SetupObjectExists("a/b/", false);
        SetupObjectExists("a/b/c/", false);

        // No objects under these prefixes
        SetupEmptyListing("a/");
        SetupEmptyListing("a/b/");
        SetupEmptyListing("a/b/c/");

        var createdKeys = new List<string>();
        _mockS3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((req, _) => createdKeys.Add(req.Key))
            .ReturnsAsync(new PutObjectResponse());

        await _provider.MkdirAsync("a/b/c", recursive: true);

        Assert.Contains("a/", createdKeys);
        Assert.Contains("a/b/", createdKeys);
        Assert.Contains("a/b/c/", createdKeys);
    }

    // --- StatAsync ---

    [Fact]
    public async Task StatAsync_ReturnsFileInfo()
    {
        var lastModified = DateTime.UtcNow;
        _mockS3.Setup(s => s.GetObjectMetadataAsync("test-bucket", "docs/file.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse
            {
                ContentLength = 42,
                LastModified = lastModified,
            });

        var stat = await _provider.StatAsync("docs/file.txt");

        Assert.Equal("file.txt", stat.Name);
        Assert.Equal("file", stat.Type);
        Assert.Equal(42, stat.Size);
    }

    [Fact]
    public async Task StatAsync_ReturnsDirectoryInfo()
    {
        SetupObjectExists("mydir", false);
        SetupObjectExists("mydir/", true);

        // GetMetadataSafeAsync for "mydir" returns NotFound, then we check "mydir/"
        _mockS3.Setup(s => s.GetObjectMetadataAsync("test-bucket", "mydir", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Not Found") { StatusCode = HttpStatusCode.NotFound });

        var stat = await _provider.StatAsync("mydir");

        Assert.Equal("mydir", stat.Name);
        Assert.Equal("directory", stat.Type);
    }

    [Fact]
    public async Task StatAsync_ThrowsWhenNotFound()
    {
        _mockS3.Setup(s => s.GetObjectMetadataAsync("test-bucket", "nope", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Not Found") { StatusCode = HttpStatusCode.NotFound });
        SetupObjectExists("nope/", false);
        SetupEmptyListing("nope/");

        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.StatAsync("nope"));
    }

    // --- ListAsync ---

    [Fact]
    public async Task ListAsync_ReturnsEntriesForRoot()
    {
        _mockS3.Setup(s => s.ListObjectsV2Async(It.Is<ListObjectsV2Request>(r =>
                r.Prefix == "" && r.Delimiter == "/"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response
            {
                IsTruncated = false,
                CommonPrefixes = new List<string> { "docs/" },
                S3Objects = new List<S3Object>
                {
                    new() { Key = "readme.txt", Size = 100, LastModified = DateTime.UtcNow },
                },
            });

        var entries = await _provider.ListAsync("/");

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Name == "docs" && e.Type == "directory");
        Assert.Contains(entries, e => e.Name == "readme.txt" && e.Type == "file");
    }

    [Fact]
    public async Task ListAsync_ThrowsWhenNotFound()
    {
        SetupObjectExists("nonexistent/", false);
        SetupEmptyListing("nonexistent/");
        SetupObjectExists("nonexistent", false);

        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.ListAsync("nonexistent"));
    }

    [Fact]
    public async Task ListAsync_ThrowsWhenNotADirectory()
    {
        SetupObjectExists("file.txt/", false);
        SetupEmptyListing("file.txt/");
        SetupObjectExists("file.txt", true);

        await Assert.ThrowsAsync<FileStorageNotADirectoryError>(
            () => _provider.ListAsync("file.txt"));
    }

    // --- RemoveAsync ---

    [Fact]
    public async Task RemoveAsync_DeletesFile()
    {
        SetupObjectExists("docs/file.txt", true);

        _mockS3.Setup(s => s.DeleteObjectAsync("test-bucket", "docs/file.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteObjectResponse());

        await _provider.RemoveAsync("docs/file.txt");

        _mockS3.Verify(s => s.DeleteObjectAsync("test-bucket", "docs/file.txt", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAsync_ThrowsWhenNotFound()
    {
        SetupObjectExists("nope", false);
        SetupObjectExists("nope/", false);
        SetupEmptyListing("nope/");

        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.RemoveAsync("nope"));
    }

    [Fact]
    public async Task RemoveAsync_NonRecursive_ThrowsOnNonEmptyDir()
    {
        SetupObjectExists("dir", false);
        SetupObjectExists("dir/", true);

        // Directory has children
        _mockS3.Setup(s => s.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(r => r.Prefix == "dir/" && r.MaxKeys == 2),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response
            {
                S3Objects = new List<S3Object>
                {
                    new() { Key = "dir/" },
                    new() { Key = "dir/file.txt" },
                },
            });

        await Assert.ThrowsAsync<FileStoragePermissionError>(
            () => _provider.RemoveAsync("dir", recursive: false));
    }

    [Fact]
    public async Task RemoveAsync_Recursive_DeletesAllObjects()
    {
        SetupObjectExists("dir", false);
        SetupObjectExists("dir/", true);

        // List returns objects to delete
        _mockS3.Setup(s => s.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(r => r.Prefix == "dir/" && r.MaxKeys == 2),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response
            {
                S3Objects = new List<S3Object>
                {
                    new() { Key = "dir/" },
                    new() { Key = "dir/file.txt" },
                },
            });

        _mockS3.Setup(s => s.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(r => r.Prefix == "dir/" && r.MaxKeys == 0),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response
            {
                IsTruncated = false,
                S3Objects = new List<S3Object>
                {
                    new() { Key = "dir/" },
                    new() { Key = "dir/file.txt" },
                },
            });

        _mockS3.Setup(s => s.DeleteObjectsAsync(It.IsAny<DeleteObjectsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteObjectsResponse());

        _mockS3.Setup(s => s.DeleteObjectAsync("test-bucket", "dir/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteObjectResponse());

        await _provider.RemoveAsync("dir", recursive: true);

        _mockS3.Verify(s => s.DeleteObjectsAsync(It.IsAny<DeleteObjectsRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // --- CopyAsync ---

    [Fact]
    public async Task CopyAsync_CopiesObject()
    {
        SetupObjectExists("src/file.txt", true);
        // Dest parent exists
        SetupObjectExists("dest/", true);

        CopyObjectRequest? capturedRequest = null;
        _mockS3.Setup(s => s.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CopyObjectRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new CopyObjectResponse());

        await _provider.CopyAsync("src/file.txt", "dest/copy.txt");

        Assert.NotNull(capturedRequest);
        Assert.Equal("src/file.txt", capturedRequest!.SourceKey);
        Assert.Equal("dest/copy.txt", capturedRequest.DestinationKey);
    }

    [Fact]
    public async Task CopyAsync_ThrowsWhenSourceNotFound()
    {
        SetupObjectExists("nonexistent.txt", false);

        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.CopyAsync("nonexistent.txt", "dest/copy.txt"));
    }

    // --- MoveAsync ---

    [Fact]
    public async Task MoveAsync_CopiesThenDeletes()
    {
        SetupObjectExists("src/file.txt", true);
        SetupObjectExists("dest/", true);

        _mockS3.Setup(s => s.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CopyObjectResponse());
        _mockS3.Setup(s => s.DeleteObjectAsync("test-bucket", "src/file.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteObjectResponse());

        await _provider.MoveAsync("src/file.txt", "dest/moved.txt");

        _mockS3.Verify(s => s.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockS3.Verify(s => s.DeleteObjectAsync("test-bucket", "src/file.txt", It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- GetDownloadStreamAsync ---

    [Fact]
    public async Task GetDownloadStreamAsync_ReturnsStream()
    {
        var content = "file content";
        SetupObjectExists("file.txt/", false);
        SetupEmptyListing("file.txt/");
        SetupGetObject("file.txt", content);

        var stream = await _provider.GetDownloadStreamAsync("file.txt");
        using var reader = new StreamReader(stream);
        var result = await reader.ReadToEndAsync();

        Assert.Equal(content, result);
    }

    [Fact]
    public async Task GetDownloadStreamAsync_ThrowsWhenNotFound()
    {
        SetupObjectExists("nope.txt/", false);
        SetupEmptyListing("nope.txt/");
        SetupGetObjectNotFound("nope.txt");

        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.GetDownloadStreamAsync("nope.txt"));
    }

    // --- UploadFileAsync ---

    [Fact]
    public async Task UploadFileAsync_DelegatesToWriteFile()
    {
        SetupObjectExists("uploads/", true);
        SetupObjectExists("uploads/data.bin/", false);
        SetupEmptyListing("uploads/data.bin/");

        PutObjectRequest? capturedRequest = null;
        _mockS3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new PutObjectResponse());

        var data = new byte[] { 0xDE, 0xAD };
        await _provider.UploadFileAsync("uploads/data.bin", data);

        Assert.NotNull(capturedRequest);
        Assert.Equal("uploads/data.bin", capturedRequest!.Key);
    }

    // --- Prefix support ---

    [Fact]
    public async Task Prefix_PrependedToKeys()
    {
        var prefixOptions = new S3ProviderOptions
        {
            Bucket = "test-bucket",
            Prefix = "myapp/data",
        };
        using var prefixProvider = new S3FileStorageProvider(prefixOptions, _mockS3.Object);

        // Setup for ExistsAsync with prefix
        _mockS3.Setup(s => s.GetObjectMetadataAsync("test-bucket", "myapp/data/file.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse());

        Assert.True(await prefixProvider.ExistsAsync("file.txt"));
    }

    // --- Extension methods ---

    [Fact]
    public void UseS3_WithOptions_SetsProvider()
    {
        var options = new FileSystemOptions();
        options.UseS3(new S3ProviderOptions { Bucket = "my-bucket" });

        Assert.NotNull(options.Provider);
        Assert.IsType<S3FileStorageProvider>(options.Provider);
        Assert.Equal("s3", options.Provider.Name);
    }

    [Fact]
    public void UseS3_WithAction_SetsProvider()
    {
        var options = new FileSystemOptions();
        options.UseS3(opts =>
        {
            opts.Bucket = "my-bucket";
            opts.Region = "eu-west-1";
        });

        Assert.NotNull(options.Provider);
        Assert.IsType<S3FileStorageProvider>(options.Provider);
        Assert.Equal("s3", options.Provider.Name);
    }

    // --- Mock helpers ---

    private void SetupObjectExists(string key, bool exists)
    {
        if (exists)
        {
            _mockS3.Setup(s => s.GetObjectMetadataAsync("test-bucket", key, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetObjectMetadataResponse());
        }
        else
        {
            _mockS3.Setup(s => s.GetObjectMetadataAsync("test-bucket", key, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonS3Exception("Not Found") { StatusCode = HttpStatusCode.NotFound });
        }
    }

    private void SetupEmptyListing(string prefix)
    {
        _mockS3.Setup(s => s.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(r => r.Prefix == prefix && r.MaxKeys == 1),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response
            {
                S3Objects = new List<S3Object>(),
                CommonPrefixes = new List<string>(),
            });
    }

    private void SetupGetObject(string key, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        _mockS3.Setup(s => s.GetObjectAsync("test-bucket", key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ResponseStream = new MemoryStream(bytes),
            });
    }

    private void SetupGetObjectNotFound(string key)
    {
        _mockS3.Setup(s => s.GetObjectAsync("test-bucket", key, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Not Found") { StatusCode = HttpStatusCode.NotFound });
    }
}
