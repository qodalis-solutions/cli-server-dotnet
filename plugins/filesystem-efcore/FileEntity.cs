namespace Qodalis.Cli.Plugin.FileSystem.EfCore;

/// <summary>
/// Entity representing a file or directory stored in the database.
/// </summary>
public class FileEntity
{
    /// <summary>
    /// Gets or sets the primary key identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the full normalized path (e.g., "/docs/readme.txt").
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Gets or sets the name of the file or directory.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Gets or sets the entry type: "file" or "directory".
    /// </summary>
    public string Type { get; set; } = "file";

    /// <summary>
    /// Gets or sets the binary content of the file, or null for directories.
    /// </summary>
    public byte[]? Content { get; set; }

    /// <summary>
    /// Gets or sets the size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the POSIX-style permissions string (e.g., "644").
    /// </summary>
    public string Permissions { get; set; } = "644";

    /// <summary>
    /// Gets or sets the creation timestamp in UTC.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the last modification timestamp in UTC.
    /// </summary>
    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Gets or sets the normalized path of the parent directory, or null for root entries.
    /// </summary>
    public string? ParentPath { get; set; }
}
