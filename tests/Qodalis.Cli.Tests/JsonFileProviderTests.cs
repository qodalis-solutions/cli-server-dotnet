using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Plugin.FileSystem.Json;

namespace Qodalis.Cli.Tests;

public class JsonFileProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public JsonFileProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qodalis-json-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "files.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private JsonFileStorageProvider CreateProvider()
    {
        return new JsonFileStorageProvider(new JsonFileStorageProviderOptions { FilePath = _filePath });
    }

    // --- Name ---

    [Fact]
    public void Name_ReturnsJsonFile()
    {
        var provider = CreateProvider();
        Assert.Equal("json-file", provider.Name);
    }

    // --- Directory operations ---

    [Fact]
    public async Task MkdirAsync_CreatesDirectory()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("test-dir");
        Assert.True(await provider.ExistsAsync("test-dir"));
    }

    [Fact]
    public async Task MkdirAsync_Recursive_CreatesNestedDirectories()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("a/b/c", recursive: true);
        Assert.True(await provider.ExistsAsync("a"));
        Assert.True(await provider.ExistsAsync("a/b"));
        Assert.True(await provider.ExistsAsync("a/b/c"));
    }

    [Fact]
    public async Task MkdirAsync_NonRecursive_ThrowsWhenParentMissing()
    {
        var provider = CreateProvider();
        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => provider.MkdirAsync("missing/child"));
    }

    [Fact]
    public async Task MkdirAsync_NonRecursive_ThrowsWhenAlreadyExists()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("dir");
        await Assert.ThrowsAsync<FileStorageExistsError>(
            () => provider.MkdirAsync("dir"));
    }

    // --- File operations ---

    [Fact]
    public async Task WriteFileAsync_And_ReadFileAsync_RoundTrip()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("docs");
        await provider.WriteFileAsync("docs/hello.txt", "Hello, World!");
        var content = await provider.ReadFileAsync("docs/hello.txt");
        Assert.Equal("Hello, World!", content);
    }

    [Fact]
    public async Task WriteFileAsync_OverwritesExistingFile()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("docs");
        await provider.WriteFileAsync("docs/file.txt", "v1");
        await provider.WriteFileAsync("docs/file.txt", "v2");
        var content = await provider.ReadFileAsync("docs/file.txt");
        Assert.Equal("v2", content);
    }

    [Fact]
    public async Task WriteFileAsync_Bytes_Works()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("bin");
        var data = new byte[] { 0x01, 0x02, 0x03 };
        await provider.WriteFileAsync("bin/data.bin", data);

        var stream = await provider.GetDownloadStreamAsync("bin/data.bin");
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal(data, ms.ToArray());
    }

    [Fact]
    public async Task ReadFileAsync_ThrowsWhenNotFound()
    {
        var provider = CreateProvider();
        await Assert.ThrowsAsync<FileStorageNotFoundError>(
            () => provider.ReadFileAsync("nonexistent.txt"));
    }

    [Fact]
    public async Task ReadFileAsync_ThrowsWhenIsDirectory()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("dir");
        await Assert.ThrowsAsync<FileStorageIsADirectoryError>(
            () => provider.ReadFileAsync("dir"));
    }

    // --- List ---

    [Fact]
    public async Task ListAsync_ReturnsEntries()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("root");
        await provider.MkdirAsync("root/subdir");
        await provider.WriteFileAsync("root/file.txt", "hello");

        var entries = await provider.ListAsync("root");
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Name == "subdir" && e.Type == "directory");
        Assert.Contains(entries, e => e.Name == "file.txt" && e.Type == "file");
    }

    [Fact]
    public async Task ListAsync_RootReturnsTopLevel()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("a");
        await provider.MkdirAsync("b");

        var entries = await provider.ListAsync("/");
        Assert.Equal(2, entries.Count);
    }

    // --- Stat ---

    [Fact]
    public async Task StatAsync_ReturnsFileInfo()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("dir");
        await provider.WriteFileAsync("dir/file.txt", "hello");

        var stat = await provider.StatAsync("dir/file.txt");
        Assert.Equal("file.txt", stat.Name);
        Assert.Equal("file", stat.Type);
        Assert.Equal(5, stat.Size);
    }

    [Fact]
    public async Task StatAsync_ReturnsDirectoryInfo()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("mydir");
        var stat = await provider.StatAsync("mydir");
        Assert.Equal("mydir", stat.Name);
        Assert.Equal("directory", stat.Type);
    }

    // --- Remove ---

    [Fact]
    public async Task RemoveAsync_DeletesFile()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("dir");
        await provider.WriteFileAsync("dir/file.txt", "data");
        await provider.RemoveAsync("dir/file.txt");
        Assert.False(await provider.ExistsAsync("dir/file.txt"));
    }

    [Fact]
    public async Task RemoveAsync_Recursive_DeletesNonEmptyDir()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("dir/sub", recursive: true);
        await provider.WriteFileAsync("dir/sub/file.txt", "data");

        await provider.RemoveAsync("dir", recursive: true);
        Assert.False(await provider.ExistsAsync("dir"));
    }

    [Fact]
    public async Task RemoveAsync_NonRecursive_ThrowsOnNonEmptyDir()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("dir");
        await provider.WriteFileAsync("dir/file.txt", "data");

        await Assert.ThrowsAsync<FileStoragePermissionError>(
            () => provider.RemoveAsync("dir", recursive: false));
    }

    // --- Copy ---

    [Fact]
    public async Task CopyAsync_CopiesFile()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("src");
        await provider.MkdirAsync("dest");
        await provider.WriteFileAsync("src/file.txt", "content");

        await provider.CopyAsync("src/file.txt", "dest/file-copy.txt");

        var content = await provider.ReadFileAsync("dest/file-copy.txt");
        Assert.Equal("content", content);
        Assert.True(await provider.ExistsAsync("src/file.txt"));
    }

    // --- Move ---

    [Fact]
    public async Task MoveAsync_MovesFile()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("src");
        await provider.MkdirAsync("dest");
        await provider.WriteFileAsync("src/file.txt", "content");

        await provider.MoveAsync("src/file.txt", "dest/moved.txt");

        Assert.False(await provider.ExistsAsync("src/file.txt"));
        var content = await provider.ReadFileAsync("dest/moved.txt");
        Assert.Equal("content", content);
    }

    // --- Persistence across instances ---

    [Fact]
    public async Task Persistence_DataSurvivesNewInstance()
    {
        var provider1 = CreateProvider();
        await provider1.MkdirAsync("docs");
        await provider1.WriteFileAsync("docs/readme.txt", "persisted content");

        // Create a new provider pointing to the same file
        var provider2 = CreateProvider();
        Assert.True(await provider2.ExistsAsync("docs"));
        Assert.True(await provider2.ExistsAsync("docs/readme.txt"));
        var content = await provider2.ReadFileAsync("docs/readme.txt");
        Assert.Equal("persisted content", content);
    }

    [Fact]
    public async Task Persistence_DirectoryStructureSurvives()
    {
        var provider1 = CreateProvider();
        await provider1.MkdirAsync("a/b/c", recursive: true);
        await provider1.WriteFileAsync("a/b/c/file.txt", "deep");

        var provider2 = CreateProvider();
        var entries = await provider2.ListAsync("a/b/c");
        Assert.Single(entries);
        Assert.Equal("file.txt", entries[0].Name);
    }

    [Fact]
    public async Task Persistence_RemovalPersists()
    {
        var provider1 = CreateProvider();
        await provider1.MkdirAsync("temp");
        await provider1.WriteFileAsync("temp/file.txt", "gone");
        await provider1.RemoveAsync("temp", recursive: true);

        var provider2 = CreateProvider();
        Assert.False(await provider2.ExistsAsync("temp"));
    }

    // --- Upload ---

    [Fact]
    public async Task UploadFileAsync_WritesContent()
    {
        var provider = CreateProvider();
        await provider.MkdirAsync("uploads");
        var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        await provider.UploadFileAsync("uploads/data.bin", data);

        var stream = await provider.GetDownloadStreamAsync("uploads/data.bin");
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal(data, ms.ToArray());
    }

    // --- Extension method ---

    [Fact]
    public void UseJsonFile_SetsProvider()
    {
        var options = new FileSystemOptions();
        options.UseJsonFile(_filePath);

        Assert.NotNull(options.Provider);
        Assert.IsType<JsonFileStorageProvider>(options.Provider);
        Assert.Equal("json-file", options.Provider.Name);
    }
}
