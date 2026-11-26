using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Controllers;
using Qodalis.Cli.Plugin.FileSystem;

namespace Qodalis.Cli.Tests;

/// <summary>An allowed root may be listed and filled, but never deleted or moved away itself.</summary>
public sealed class OsFileStorageProviderRootTests : IDisposable
{
    private readonly string _root;
    private readonly string _other;
    private readonly OsFileStorageProvider _provider;

    public OsFileStorageProviderRootTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "qcli-roots-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "root");
        _other = Path.Combine(baseDir, "other");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_other);
        File.WriteAllText(Path.Combine(_root, "keep.txt"), "data");
        _provider = new OsFileStorageProvider(new OsProviderOptions { AllowedPaths = [_root, _other] });
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
    }

    public static TheoryData<string> RootSpellings => new()
    {
        "",                  // the root as configured
        "/",                 // trailing separator
        "/.",                // dot segment
        "/sub/..",           // parent segment
        "//",                // doubled separator
    };

    [Theory]
    [MemberData(nameof(RootSpellings))]
    public async Task RemovingTheRootIsRefused(string suffix)
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        await Assert.ThrowsAsync<FileStoragePermissionError>(() => _provider.RemoveAsync(_root + suffix, recursive: true));

        Assert.True(Directory.Exists(_root));
        Assert.True(File.Exists(Path.Combine(_root, "keep.txt")));
    }

    [Fact]
    public async Task MovingTheRootIsRefused()
    {
        await Assert.ThrowsAsync<FileStoragePermissionError>(() => _provider.MoveAsync(_root, Path.Combine(_other, "moved")));

        Assert.True(Directory.Exists(_root));
        Assert.False(Directory.Exists(Path.Combine(_other, "moved")));
    }

    [Fact]
    public async Task RenamingTheRootIsRefused()
    {
        await Assert.ThrowsAsync<FileStoragePermissionError>(() => _provider.MoveAsync(_root, _root + "-renamed"));

        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task RemovingASymlinkToAnotherRootIsRefused()
    {
        var link = Path.Combine(_root, "link-to-other");
        try { Directory.CreateSymbolicLink(link, _other); }
        catch (Exception) { return; } // symlinks unavailable (e.g. Windows without privilege)

        await Assert.ThrowsAsync<FileStoragePermissionError>(() => _provider.RemoveAsync(link, recursive: true));
        await Assert.ThrowsAsync<FileStoragePermissionError>(() => _provider.MoveAsync(link, Path.Combine(_root, "renamed")));
        Assert.True(Directory.Exists(_other));
    }

    [Fact]
    public async Task ContentsOfTheRootCanStillBeRemovedAndMoved()
    {
        var dir = Path.Combine(_root, "dir");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "a");

        await _provider.MoveAsync(Path.Combine(_root, "keep.txt"), Path.Combine(_root, "kept.txt"));
        await _provider.RemoveAsync(dir, recursive: true);

        Assert.False(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(_root, "kept.txt")));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task DeleteEndpointAnswers403ForTheRoot()
    {
        var controller = new FileSystemController(_provider, NullLogger<FileSystemController>.Instance);

        var result = await controller.Delete(_root, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, status.StatusCode);
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task AFolderAboveAllowedRoots_ListsOnlyTheFoldersLeadingToThem()
    {
        // The dashboard's Files page opens at "/": with roots set, it shows the way down to them instead of "Access denied"
        var baseDir = Path.GetDirectoryName(_root)!;
        File.WriteAllText(Path.Combine(baseDir, "secret.txt"), "x");
        Directory.CreateDirectory(Path.Combine(baseDir, "hidden"));
        var parent = await _provider.ListAsync(baseDir);
        Assert.Equal(["other", "root"], parent.Select(e => e.Name).Order());
        Assert.All(parent, e => Assert.Equal(("directory", 0L, ""), (e.Type, e.Size, e.Modified)));

        var top = await _provider.ListAsync("/");
        var first = Path.GetFullPath(_root).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Equal([first], top.Select(e => e.Name));

        // Below the way down, beside it, and files stay refused
        await Assert.ThrowsAsync<FileStoragePermissionError>(() => _provider.ListAsync(Path.Combine(baseDir, "hidden")));
        await Assert.ThrowsAsync<FileStoragePermissionError>(() => _provider.ListAsync(Path.Combine(baseDir, "secret.txt")));
        // A denied root is not shown
        var denied = new OsFileStorageProvider(new OsProviderOptions { AllowedPaths = [_root, _other], DeniedPaths = [_other] });
        Assert.Equal(["root"], (await denied.ListAsync(baseDir)).Select(e => e.Name));
        // Inside a root: the real listing
        Assert.Contains(await _provider.ListAsync(_root), e => e.Name == "keep.txt" && e.Type == "file");
    }
}
