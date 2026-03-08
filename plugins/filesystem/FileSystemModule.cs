using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.FileSystem;

public class FileSystemModule : ICliModule
{
    public string Name => "filesystem";
    public string Version => "1.0.0";
    public string Description => "Provides pluggable file storage with in-memory and OS providers";
    public ICliCommandAuthor Author => DefaultLibraryAuthor.Instance;
    public IEnumerable<ICliCommandProcessor> Processors => [];
}
