using Qodalis.Cli.FileSystem;
using Qodalis.Cli.Plugin.FileSystem;

namespace Qodalis.Cli.Tests;

public class AllowedPathMatcherTests : IDisposable
{
    private readonly string _base;
    private readonly string _allowed;
    private readonly string _sibling;

    public AllowedPathMatcherTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "qcli-paths-" + Guid.NewGuid().ToString("N"));
        _allowed = Path.Combine(_base, "share");
        _sibling = Path.Combine(_base, "share-other");
        Directory.CreateDirectory(_allowed);
        Directory.CreateDirectory(_sibling);
        File.WriteAllText(Path.Combine(_allowed, "in.txt"), "in");
        File.WriteAllText(Path.Combine(_sibling, "secret.txt"), "secret");
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    [Fact]
    public void AllowsTheRootAndPathsBelowIt()
    {
        Assert.True(AllowedPathMatcher.IsAllowed(_allowed, [_allowed]));
        Assert.True(AllowedPathMatcher.IsAllowed(_allowed + Path.DirectorySeparatorChar, [_allowed]));
        Assert.True(AllowedPathMatcher.IsAllowed(Path.Combine(_allowed, "in.txt"), [_allowed]));
        Assert.True(AllowedPathMatcher.IsAllowed(Path.Combine(_allowed, "new", "file.txt"), [_allowed]));
    }

    [Fact]
    public void RejectsASiblingThatSharesTheRootPrefix()
    {
        Assert.False(AllowedPathMatcher.IsAllowed(Path.Combine(_sibling, "secret.txt"), [_allowed]));
        Assert.False(AllowedPathMatcher.IsAllowed(_sibling, [_allowed]));
    }

    [Fact]
    public void RejectsTraversalOutOfTheRoot()
    {
        var escape = _allowed + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "share-other";
        Assert.False(AllowedPathMatcher.IsAllowed(escape, [_allowed]));
    }

    [Fact]
    public void RejectsSymlinksThatLeaveTheRoot()
    {
        if (OperatingSystem.IsWindows()) return; // creating links needs privileges there

        var dirLink = Path.Combine(_allowed, "out");
        Directory.CreateSymbolicLink(dirLink, _sibling);
        var fileLink = Path.Combine(_allowed, "secret-link.txt");
        File.CreateSymbolicLink(fileLink, Path.Combine(_sibling, "secret.txt"));

        Assert.False(AllowedPathMatcher.IsAllowed(Path.Combine(dirLink, "secret.txt"), [_allowed]));
        Assert.False(AllowedPathMatcher.IsAllowed(Path.Combine(dirLink, "new.txt"), [_allowed]));
        Assert.False(AllowedPathMatcher.IsAllowed(fileLink, [_allowed]));
    }

    [Fact]
    public void FollowsSymlinksThatStayInsideTheRoot()
    {
        if (OperatingSystem.IsWindows()) return;

        var inner = Path.Combine(_allowed, "inner");
        Directory.CreateDirectory(inner);
        var link = Path.Combine(_allowed, "alias");
        Directory.CreateSymbolicLink(link, inner);

        Assert.True(AllowedPathMatcher.IsAllowed(Path.Combine(link, "file.txt"), [_allowed]));
    }

    [Fact]
    public void AcceptsAnAllowedRootThatIsItselfASymlink()
    {
        if (OperatingSystem.IsWindows()) return;

        var rootLink = Path.Combine(_base, "share-link");
        Directory.CreateSymbolicLink(rootLink, _allowed);

        Assert.True(AllowedPathMatcher.IsAllowed(Path.Combine(_allowed, "in.txt"), [rootLink]));
        Assert.True(AllowedPathMatcher.IsAllowed(Path.Combine(rootLink, "in.txt"), [rootLink]));
    }

    [Fact]
    public void RejectsALinkLoop()
    {
        if (OperatingSystem.IsWindows()) return;

        var a = Path.Combine(_allowed, "a");
        var b = Path.Combine(_allowed, "b");
        File.CreateSymbolicLink(a, b);
        File.CreateSymbolicLink(b, a);

        Assert.False(AllowedPathMatcher.IsAllowed(a, [_allowed]));
    }

    [Fact]
    public async Task OsProviderRejectsThePrefixSibling()
    {
        var provider = new OsFileStorageProvider(new OsProviderOptions { AllowedPaths = [_allowed] });

        await Assert.ThrowsAsync<FileStoragePermissionError>(
            () => provider.ReadFileAsync(Path.Combine(_sibling, "secret.txt")));
        Assert.Equal("in", await provider.ReadFileAsync(Path.Combine(_allowed, "in.txt")));
    }

    [Fact]
    public void PathValidatorRejectsThePrefixSibling()
    {
        var validator = new FileSystemPathValidator(new FileSystemOptions { AllowedPaths = [_allowed] });

        Assert.Null(validator.Validate(Path.Combine(_sibling, "secret.txt")));
        Assert.NotNull(validator.Validate(Path.Combine(_allowed, "in.txt")));
    }
}
