namespace Qodalis.Cli.Plugin.FileSystem;

/// <summary>
/// Represents detailed metadata for a file or directory.
/// </summary>
public class FileStat
{
    /// <summary>
    /// Gets or sets the name of the file or directory.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Gets or sets the entry type ("file" or "directory").
    /// </summary>
    public string Type { get; set; } = "file";

    /// <summary>
    /// Gets or sets the size in bytes (0 for directories).
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the creation timestamp in ISO 8601 format.
    /// </summary>
    public string Created { get; set; } = "";

    /// <summary>
    /// Gets or sets the last modification timestamp in ISO 8601 format.
    /// </summary>
    public string Modified { get; set; } = "";

    /// <summary>
    /// Gets or sets the file permissions string, if available.
    /// </summary>
    public string? Permissions { get; set; }
}
