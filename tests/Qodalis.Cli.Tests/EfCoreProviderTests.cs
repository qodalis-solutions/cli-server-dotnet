using Microsoft.EntityFrameworkCore;
using Qodalis.Cli.FileSystem;
using Qodalis.Cli.FileSystem.EfCore;

namespace Qodalis.Cli.Tests;

public class EfCoreProviderTests : IDisposable
{
    private readonly EfCoreFileStorageProvider _provider;
    private readonly FileStorageDbContext _db;

    public EfCoreProviderTests()
    {
        var options = new DbContextOptionsBuilder<FileStorageDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new FileStorageDbContext(options);
        _provider = new EfCoreFileStorageProvider(_db);
    }

    public void Dispose()
    {
        _provider.Dispose();
    }

    // --- Name ---

    [Fact]
    public void Name_ReturnsEfCore()
    {
        Assert.Equal("efcore", _provider.Name);
    }

    // --- Directory operations ---

    [Fact]
    public async Task MkdirAsync_CreatesDirectory()
    {
        await _provider.MkdirAsync("test-dir");
        Assert.True(await _provider.ExistsAsync("test-dir"));
    }

    [Fact]
    public async Task MkdirAsync_Recursive_CreatesNestedDirectories()
    {
        await _provider.MkdirAsync("a/b/c", recursive: true);
        Assert.True(await _provider.ExistsAsync("a"));
        Assert.True(await _provider.ExistsAsync("a/b"));
        Assert.True(await _provider.ExistsAsync("a/b/c"));
    }

    [Fact]
    public async Task MkdirAsync_NonRecursive_ThrowsWhenParentMissing()
    {
        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.MkdirAsync("missing/child"));
    }

    [Fact]
    public async Task MkdirAsync_NonRecursive_ThrowsWhenAlreadyExists()
    {
        await _provider.MkdirAsync("dir");
        await Assert.ThrowsAsync<FileStorageExistsError>(
            () => _provider.MkdirAsync("dir"));
    }

    // --- File operations ---

    [Fact]
    public async Task WriteFileAsync_And_ReadFileAsync_RoundTrip()
    {
        await _provider.MkdirAsync("docs");
        await _provider.WriteFileAsync("docs/hello.txt", "Hello, World!");
        var content = await _provider.ReadFileAsync("docs/hello.txt");
        Assert.Equal("Hello, World!", content);
    }

    [Fact]
    public async Task WriteFileAsync_OverwritesExistingFile()
    {
        await _provider.MkdirAsync("docs");
        await _provider.WriteFileAsync("docs/file.txt", "v1");
        await _provider.WriteFileAsync("docs/file.txt", "v2");
        var content = await _provider.ReadFileAsync("docs/file.txt");
        Assert.Equal("v2", content);
    }

    [Fact]
    public async Task WriteFileAsync_Bytes_Works()
    {
        await _provider.MkdirAsync("bin");
        var data = new byte[] { 0x01, 0x02, 0x03 };
        await _provider.WriteFileAsync("bin/data.bin", data);

        var stream = await _provider.GetDownloadStreamAsync("bin/data.bin");
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal(data, ms.ToArray());
    }

    [Fact]
    public async Task ReadFileAsync_ThrowsWhenNotFound()
    {
        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.ReadFileAsync("nonexistent.txt"));
    }

    [Fact]
    public async Task ReadFileAsync_ThrowsWhenIsDirectory()
    {
        await _provider.MkdirAsync("dir");
        await Assert.ThrowsAsync<FileStorageIsADirectoryError>(
            () => _provider.ReadFileAsync("dir"));
    }

    [Fact]
    public async Task WriteFileAsync_ThrowsWhenParentMissing()
    {
        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.WriteFileAsync("missing/file.txt", "content"));
    }

    // --- List ---

    [Fact]
    public async Task ListAsync_ReturnsEntries()
    {
        await _provider.MkdirAsync("root");
        await _provider.MkdirAsync("root/subdir");
        await _provider.WriteFileAsync("root/file.txt", "hello");

        var entries = await _provider.ListAsync("root");
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Name == "subdir" && e.Type == "directory");
        Assert.Contains(entries, e => e.Name == "file.txt" && e.Type == "file");
    }

    [Fact]
    public async Task ListAsync_ThrowsWhenNotFound()
    {
        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.ListAsync("nonexistent"));
    }

    [Fact]
    public async Task ListAsync_ThrowsWhenNotADirectory()
    {
        await _provider.MkdirAsync("dir");
        await _provider.WriteFileAsync("dir/file.txt", "data");

        await Assert.ThrowsAsync<FileStorageNotADirectoryError>(
            () => _provider.ListAsync("dir/file.txt"));
    }

    [Fact]
    public async Task ListAsync_RootReturnsTopLevel()
    {
        await _provider.MkdirAsync("a");
        await _provider.MkdirAsync("b");

        var entries = await _provider.ListAsync("/");
        Assert.Equal(2, entries.Count);
    }

    // --- Stat ---

    [Fact]
    public async Task StatAsync_ReturnsFileInfo()
    {
        await _provider.MkdirAsync("dir");
        await _provider.WriteFileAsync("dir/file.txt", "hello");

        var stat = await _provider.StatAsync("dir/file.txt");
        Assert.Equal("file.txt", stat.Name);
        Assert.Equal("file", stat.Type);
        Assert.Equal(5, stat.Size);
    }

    [Fact]
    public async Task StatAsync_ReturnsDirectoryInfo()
    {
        await _provider.MkdirAsync("mydir");
        var stat = await _provider.StatAsync("mydir");
        Assert.Equal("mydir", stat.Name);
        Assert.Equal("directory", stat.Type);
    }

    [Fact]
    public async Task StatAsync_ThrowsWhenNotFound()
    {
        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.StatAsync("nope"));
    }

    // --- Remove ---

    [Fact]
    public async Task RemoveAsync_DeletesFile()
    {
        await _provider.MkdirAsync("dir");
        await _provider.WriteFileAsync("dir/file.txt", "data");
        await _provider.RemoveAsync("dir/file.txt");
        Assert.False(await _provider.ExistsAsync("dir/file.txt"));
    }

    [Fact]
    public async Task RemoveAsync_DeletesEmptyDirectory()
    {
        await _provider.MkdirAsync("empty");
        await _provider.RemoveAsync("empty");
        Assert.False(await _provider.ExistsAsync("empty"));
    }

    [Fact]
    public async Task RemoveAsync_NonRecursive_ThrowsOnNonEmptyDir()
    {
        await _provider.MkdirAsync("dir");
        await _provider.WriteFileAsync("dir/file.txt", "data");

        await Assert.ThrowsAsync<FileStoragePermissionError>(
            () => _provider.RemoveAsync("dir", recursive: false));
    }

    [Fact]
    public async Task RemoveAsync_Recursive_DeletesNonEmptyDir()
    {
        await _provider.MkdirAsync("dir/sub", recursive: true);
        await _provider.WriteFileAsync("dir/sub/file.txt", "data");

        await _provider.RemoveAsync("dir", recursive: true);
        Assert.False(await _provider.ExistsAsync("dir"));
        Assert.False(await _provider.ExistsAsync("dir/sub"));
        Assert.False(await _provider.ExistsAsync("dir/sub/file.txt"));
    }

    [Fact]
    public async Task RemoveAsync_ThrowsWhenNotFound()
    {
        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.RemoveAsync("nonexistent"));
    }

    // --- Copy ---

    [Fact]
    public async Task CopyAsync_CopiesFile()
    {
        await _provider.MkdirAsync("src");
        await _provider.MkdirAsync("dest");
        await _provider.WriteFileAsync("src/file.txt", "content");

        await _provider.CopyAsync("src/file.txt", "dest/file-copy.txt");

        var content = await _provider.ReadFileAsync("dest/file-copy.txt");
        Assert.Equal("content", content);

        // Original still exists
        Assert.True(await _provider.ExistsAsync("src/file.txt"));
    }

    [Fact]
    public async Task CopyAsync_ThrowsWhenSourceNotFound()
    {
        await _provider.MkdirAsync("dest");
        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.CopyAsync("nonexistent.txt", "dest/copy.txt"));
    }

    // --- Move ---

    [Fact]
    public async Task MoveAsync_MovesFile()
    {
        await _provider.MkdirAsync("src");
        await _provider.MkdirAsync("dest");
        await _provider.WriteFileAsync("src/file.txt", "content");

        await _provider.MoveAsync("src/file.txt", "dest/moved.txt");

        Assert.False(await _provider.ExistsAsync("src/file.txt"));
        var content = await _provider.ReadFileAsync("dest/moved.txt");
        Assert.Equal("content", content);
    }

    [Fact]
    public async Task MoveAsync_ThrowsWhenSourceNotFound()
    {
        await _provider.MkdirAsync("dest");
        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.MoveAsync("nonexistent.txt", "dest/moved.txt"));
    }

    // --- Exists ---

    [Fact]
    public async Task ExistsAsync_ReturnsFalseForNonexistent()
    {
        Assert.False(await _provider.ExistsAsync("nope"));
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueForDirectory()
    {
        await _provider.MkdirAsync("dir");
        Assert.True(await _provider.ExistsAsync("dir"));
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueForFile()
    {
        await _provider.MkdirAsync("dir");
        await _provider.WriteFileAsync("dir/file.txt", "data");
        Assert.True(await _provider.ExistsAsync("dir/file.txt"));
    }

    // --- Download stream ---

    [Fact]
    public async Task GetDownloadStreamAsync_ThrowsWhenNotFound()
    {
        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => _provider.GetDownloadStreamAsync("nonexistent.txt"));
    }

    [Fact]
    public async Task GetDownloadStreamAsync_ThrowsWhenIsDirectory()
    {
        await _provider.MkdirAsync("dir");
        await Assert.ThrowsAsync<FileStorageIsADirectoryError>(
            () => _provider.GetDownloadStreamAsync("dir"));
    }

    // --- Upload ---

    [Fact]
    public async Task UploadFileAsync_WritesContent()
    {
        await _provider.MkdirAsync("uploads");
        var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        await _provider.UploadFileAsync("uploads/data.bin", data);

        var stream = await _provider.GetDownloadStreamAsync("uploads/data.bin");
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal(data, ms.ToArray());
    }

    // --- Extension method ---

    [Fact]
    public void UseEfCore_SetsProvider()
    {
        var dbOptions = new DbContextOptionsBuilder<FileStorageDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var options = new FileSystemOptions();
        options.UseEfCore(dbOptions);

        Assert.NotNull(options.Provider);
        Assert.IsType<EfCoreFileStorageProvider>(options.Provider);
        Assert.Equal("efcore", options.Provider.Name);
    }
}
