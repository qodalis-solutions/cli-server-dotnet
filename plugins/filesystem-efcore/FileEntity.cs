namespace Qodalis.Cli.Plugin.FileSystem.EfCore;

public class FileEntity
{
    public int Id { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "file";  // "file" or "directory"
    public byte[]? Content { get; set; }
    public long Size { get; set; }
    public string Permissions { get; set; } = "644";
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string? ParentPath { get; set; }
}
