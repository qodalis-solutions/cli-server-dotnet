namespace Qodalis.Cli.Plugin.FileSystem;

public class FileSystemOptions
{
    public List<string> AllowedPaths { get; set; } = [];
    public IFileStorageProvider? Provider { get; set; }
}
