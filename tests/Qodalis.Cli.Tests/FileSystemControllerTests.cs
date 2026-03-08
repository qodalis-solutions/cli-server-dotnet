using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Controllers;
using Qodalis.Cli.Plugin.FileSystem;

namespace Qodalis.Cli.Tests;

public class FileSystemControllerTests
{
    private readonly InMemoryFileStorageProvider _provider;
    private readonly FileSystemController _controller;

    public FileSystemControllerTests()
    {
        _provider = new InMemoryFileStorageProvider();
        _controller = new FileSystemController(_provider);
    }

    // --- ListDirectory ---

    [Fact]
    public async Task ListDirectory_ReturnsEntries()
    {
        await _provider.MkdirAsync("root");
        await _provider.WriteFileAsync("root/file.txt", "hello");
        await _provider.MkdirAsync("root/sub");

        var result = await _controller.ListDirectory("root", CancellationToken.None) as OkObjectResult;
        Assert.NotNull(result);

        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("file.txt", json);
        Assert.Contains("sub", json);
    }

    [Fact]
    public async Task ListDirectory_Returns404_WhenNotFound()
    {
        var result = await _controller.ListDirectory("nonexistent", CancellationToken.None);
        var notFound = result as NotFoundObjectResult;
        Assert.NotNull(notFound);
    }

    // --- ReadFile ---

    [Fact]
    public async Task ReadFile_ReturnsContent()
    {
        await _provider.MkdirAsync("docs");
        await _provider.WriteFileAsync("docs/test.txt", "file content");

        var result = await _controller.ReadFile("docs/test.txt", CancellationToken.None) as OkObjectResult;
        Assert.NotNull(result);

        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("file content", json);
    }

    [Fact]
    public async Task ReadFile_Returns404_WhenNotFound()
    {
        var result = await _controller.ReadFile("missing.txt", CancellationToken.None);
        var notFound = result as NotFoundObjectResult;
        Assert.NotNull(notFound);
    }

    [Fact]
    public async Task ReadFile_Returns400_WhenIsDirectory()
    {
        await _provider.MkdirAsync("adir");
        var result = await _controller.ReadFile("adir", CancellationToken.None);
        var objResult = result as ObjectResult;
        Assert.NotNull(objResult);
        Assert.Equal(400, objResult.StatusCode);
    }

    // --- GetFileInfo (stat) ---

    [Fact]
    public async Task Stat_ReturnsFileInfo()
    {
        await _provider.MkdirAsync("dir");
        await _provider.WriteFileAsync("dir/file.txt", "data");

        var result = await _controller.GetFileInfo("dir/file.txt", CancellationToken.None) as OkObjectResult;
        Assert.NotNull(result);

        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("file.txt", json);
        Assert.Contains("file", json);
    }

    [Fact]
    public async Task Stat_Returns404_WhenNotFound()
    {
        var result = await _controller.GetFileInfo("nope", CancellationToken.None);
        var notFound = result as NotFoundObjectResult;
        Assert.NotNull(notFound);
    }

    // --- CreateDirectory (mkdir) ---

    [Fact]
    public async Task Mkdir_CreatesDirectory()
    {
        var request = new CreateDirectoryRequest { Path = "newdir" };
        var result = await _controller.CreateDirectory(request, CancellationToken.None) as OkObjectResult;
        Assert.NotNull(result);

        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("true", json, StringComparison.OrdinalIgnoreCase);

        Assert.True(await _provider.ExistsAsync("newdir"));
    }

    [Fact]
    public async Task Mkdir_ReturnsCreatedFalse_WhenAlreadyExists()
    {
        await _provider.MkdirAsync("existing");
        var request = new CreateDirectoryRequest { Path = "existing" };
        var result = await _controller.CreateDirectory(request, CancellationToken.None) as OkObjectResult;
        Assert.NotNull(result);

        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("false", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mkdir_ReturnsBadRequest_WhenPathEmpty()
    {
        var request = new CreateDirectoryRequest { Path = "" };
        var result = await _controller.CreateDirectory(request, CancellationToken.None);
        var badRequest = result as BadRequestObjectResult;
        Assert.NotNull(badRequest);
    }

    // --- Delete (rm) ---

    [Fact]
    public async Task Delete_DeletesFile()
    {
        await _provider.MkdirAsync("dir");
        await _provider.WriteFileAsync("dir/file.txt", "content");

        var result = await _controller.Delete("dir/file.txt", CancellationToken.None) as OkObjectResult;
        Assert.NotNull(result);

        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("file", json);

        Assert.False(await _provider.ExistsAsync("dir/file.txt"));
    }

    [Fact]
    public async Task Delete_DeletesDirectory()
    {
        await _provider.MkdirAsync("dir/sub", recursive: true);
        await _provider.WriteFileAsync("dir/sub/file.txt", "content");

        var result = await _controller.Delete("dir", CancellationToken.None) as OkObjectResult;
        Assert.NotNull(result);

        Assert.False(await _provider.ExistsAsync("dir"));
    }

    [Fact]
    public async Task Delete_Returns404_WhenNotFound()
    {
        var result = await _controller.Delete("nonexistent", CancellationToken.None);
        var notFound = result as NotFoundObjectResult;
        Assert.NotNull(notFound);
    }
}
