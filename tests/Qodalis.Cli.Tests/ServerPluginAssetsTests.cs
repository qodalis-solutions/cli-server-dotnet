using System.IO.Compression;
using System.Reflection;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Plugins;

namespace Qodalis.Cli.Tests;

/// <summary>
/// Runtime-specific and satellite assets of server plugins: the RID fallback chain, which package entries are taken, and
/// how the plugin's load context finds managed and native files in its folder.
/// </summary>
public sealed class ServerPluginAssetsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qcli-plugin-assets-{Guid.NewGuid():N}");

    public ServerPluginAssetsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("osx-arm64", "osx-arm64,osx,unix,any")]
    [InlineData("linux-x64", "linux-x64,linux,unix,any")]
    [InlineData("linux-musl-x64", "linux-musl-x64,linux-musl,linux-x64,linux,unix,any")]
    [InlineData("win-x64", "win-x64,win,any")]
    [InlineData("osx.14-arm64", "osx.14-arm64,osx-arm64,osx,unix,any")]
    [InlineData("ubuntu.22.04-x64", "ubuntu.22.04-x64,ubuntu-x64,linux-x64,linux,unix,any")]
    public void RuntimeIdentifierChain_FallsBackAsNuGetsPortableGraph(string rid, string chain) =>
        Assert.Equal(chain.Split(','), CliPluginLoadContext.RuntimeIdentifierChain(rid));

    [Fact]
    public void ThisRuntime_IsFirst_AndAnyLast()
    {
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, CliPluginLoadContext.RuntimeIdentifiers[0]);
        Assert.Equal("any", CliPluginLoadContext.RuntimeIdentifiers[^1]);
    }

    private static ZipArchive Zip(params string[] entries)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in entries)
                using (var s = zip.CreateEntry(entry).Open()) s.WriteByte(1);
        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }

    [Fact]
    public void RuntimeFiles_TheChainsNativeFiles_AndTheBestFrameworkOfEachRidsLib()
    {
        using var zip = Zip(
            "runtimes/osx-arm64/native/libe_sqlite3.dylib",
            "runtimes/osx/native/libother.dylib",
            "runtimes/linux-x64/native/libe_sqlite3.so",
            "runtimes/osx-arm64/native/sub/deep.dylib",
            "runtimes/osx-arm64/lib/netstandard2.0/Acme.Rid.dll",
            "runtimes/osx-arm64/lib/net8.0/Acme.Rid.dll",
            "runtimes/osx-arm64/lib/net8.0/readme.txt",
            "runtimes/unix/lib/netcoreapp3.1/Acme.Unix.dll",
            "runtimes/win/lib/net8.0/Acme.Win.dll",
            "lib/net8.0/Acme.dll");
        var files = NuGetPackage.RuntimeFiles(zip, ["osx-arm64", "osx", "unix", "any"]).Select(f => f.Path).Order(StringComparer.Ordinal);
        Assert.Equal(new[]
        {
            "runtimes/osx-arm64/lib/net8.0/Acme.Rid.dll",
            "runtimes/osx-arm64/native/libe_sqlite3.dylib",
            "runtimes/osx/native/libother.dylib",
            "runtimes/unix/lib/netcoreapp3.1/Acme.Unix.dll",
        }, files);
    }

    [Fact]
    public void SatelliteFiles_TheCultureFoldersOfTheLibFolder()
    {
        using var zip = Zip("lib/net8.0/Acme.dll", "lib/net8.0/fr/Acme.resources.dll", "lib/net8.0/zh-Hans/Acme.resources.dll",
            "lib/net8.0/fr/notes.txt", "lib/netstandard2.0/de/Acme.resources.dll", "lib/net8.0/a/b/Acme.resources.dll");
        Assert.Equal(["fr/Acme.resources.dll", "zh-Hans/Acme.resources.dll"],
            NuGetPackage.SatelliteFiles(zip, "lib/net8.0/").Select(s => s.Path).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("runtimes/osx-arm64/native/libe_sqlite3.dylib", true)]
    [InlineData("fr/Acme.resources.dll", true)]
    [InlineData("runtimes/C:x/native/a.dll", false)]
    [InlineData("runtimes/../../a.dll", false)]
    [InlineData("runtimes//a.dll", false)]
    [InlineData("runtimes\\x\\a.dll", false)]
    [InlineData("", false)]
    public void ResolveRelative_EverySegmentAPlainFileName(string relative, bool safe)
    {
        var resolved = PluginFilePaths.ResolveRelative(_dir, relative);
        if (safe) Assert.Equal(Path.Combine([Path.GetFullPath(_dir), .. relative.Split('/')]), resolved);
        else Assert.Null(resolved);
    }

    private void Touch(string relative)
    {
        var path = Path.Combine([_dir, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
    }

    private string At(string relative) => Path.Combine([_dir, .. relative.Split('/')]);

    [Fact]
    public void ResolveUnmanaged_ThisRuntimesNativeFolder_ThenItsFallbacks_ThenTheFolder_WithThePlatformsNames()
    {
        var rids = CliPluginLoadContext.RuntimeIdentifiers;
        var ext = OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
        var prefix = OperatingSystem.IsWindows() ? "" : "lib";
        Touch($"runtimes/{rids[1]}/native/{prefix}e_sqlite3{ext}");
        Touch($"runtimes/{rids[0]}/native/{prefix}e_sqlite3{ext}");
        Touch($"runtimes/{rids[1]}/native/{prefix}family{ext}");
        Touch($"{prefix}top{ext}");
        Touch("runtimes/not-this-one/native/elsewhere.dll");
        var context = new CliPluginLoadContext("assets", _dir);
        try
        {
            Assert.Equal(At($"runtimes/{rids[0]}/native/{prefix}e_sqlite3{ext}"), context.ResolveUnmanaged("e_sqlite3"));
            Assert.Equal(At($"runtimes/{rids[1]}/native/{prefix}family{ext}"), context.ResolveUnmanaged("family"));
            Assert.Equal(At($"{prefix}top{ext}"), context.ResolveUnmanaged("top"));
            Assert.Null(context.ResolveUnmanaged("elsewhere"));
            Assert.Null(context.ResolveUnmanaged("../top"));
            Assert.Null(context.ResolveUnmanaged(At($"{prefix}top{ext}")));
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void ResolveManaged_ASatelliteInItsCulture_ThisRuntimesLibFirst_ThenTheLibOne_NeverTheHosts()
    {
        var rids = CliPluginLoadContext.RuntimeIdentifiers;
        Touch("Acme.Thing.dll");
        Touch($"runtimes/{rids[1]}/lib/net8.0/Acme.Thing.dll");
        Touch("Acme.Plain.dll");
        Touch("fr/Acme.Thing.resources.dll");
        Touch("Qodalis.Cli.Abstractions.dll");
        var context = new CliPluginLoadContext("assets", _dir);
        try
        {
            Assert.Equal(At($"runtimes/{rids[1]}/lib/net8.0/Acme.Thing.dll"), context.ResolveManaged(new AssemblyName("Acme.Thing")));
            Assert.Equal(At("Acme.Plain.dll"), context.ResolveManaged(new AssemblyName("Acme.Plain")));
            Assert.Equal(At("fr/Acme.Thing.resources.dll"), context.ResolveManaged(new AssemblyName("Acme.Thing.resources, Culture=fr")));
            Assert.Null(context.ResolveManaged(new AssemblyName("Acme.Thing.resources, Culture=de")));
            Assert.Null(context.ResolveManaged(new AssemblyName("Qodalis.Cli.Abstractions")));
            Assert.Null(context.ResolveManaged(new AssemblyName("Acme.Missing")));
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void HostVersion_TheInformationalVersionWithoutMetadata()
    {
        var version = CliPluginLoadContext.HostVersion("Microsoft.Extensions.Logging.Abstractions");
        Assert.NotNull(version);
        Assert.StartsWith("10.", version);
        Assert.DoesNotContain("+", version);
        Assert.Null(CliPluginLoadContext.HostVersion("Acme.Nowhere"));
    }
}
