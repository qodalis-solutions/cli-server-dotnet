namespace Qodalis.Cli.Plugin.FileSystem;

/// <summary>
/// Configuration options for the file system plugin, including provider selection and path restrictions.
/// </summary>
public class FileSystemOptions
{
    /// <summary>
    /// Gets or sets the list of allowed filesystem paths for path validation and access control.
    /// </summary>
    public List<string> AllowedPaths { get; set; } = [];

    /// <summary>
    /// Gets or sets the file storage provider to use for file operations.
    /// </summary>
    public IFileStorageProvider? Provider { get; set; }
}
