namespace Qodalis.Cli.FileSystem;

public class FileEntry
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "file";
    public long Size { get; set; }
    public string Modified { get; set; } = "";
    public string? Permissions { get; set; }
}
