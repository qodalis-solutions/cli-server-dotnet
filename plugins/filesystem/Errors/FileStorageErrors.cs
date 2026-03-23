namespace Qodalis.Cli.Plugin.FileSystem;

/// <summary>
/// Thrown when a file or directory is not found at the specified path.
/// </summary>
public class FileStorageNotFoundError : Exception
{
    /// <summary>
    /// Gets the path that was not found.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Initializes a new instance with the path that could not be found.
    /// </summary>
    /// <param name="path">The path that does not exist.</param>
    public FileStorageNotFoundError(string path) : base($"Path not found: {path}") { Path = path; }
}

/// <summary>
/// Thrown when access to a path is denied due to permission or validation restrictions.
/// </summary>
public class FileStoragePermissionError : Exception
{
    /// <summary>
    /// Gets the path for which access was denied.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Initializes a new instance with the path that was denied access.
    /// </summary>
    /// <param name="path">The path that triggered the permission error.</param>
    public FileStoragePermissionError(string path) : base($"Access denied: {path}") { Path = path; }
}

/// <summary>
/// Thrown when attempting to create a file or directory at a path that already exists.
/// </summary>
public class FileStorageExistsError : Exception
{
    /// <summary>
    /// Gets the path that already exists.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Initializes a new instance with the path that already exists.
    /// </summary>
    /// <param name="path">The conflicting path.</param>
    public FileStorageExistsError(string path) : base($"Path already exists: {path}") { Path = path; }
}

/// <summary>
/// Thrown when a directory operation is performed on a path that is not a directory.
/// </summary>
public class FileStorageNotADirectoryError : Exception
{
    /// <summary>
    /// Gets the path that is not a directory.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Initializes a new instance with the path that is not a directory.
    /// </summary>
    /// <param name="path">The path that is not a directory.</param>
    public FileStorageNotADirectoryError(string path) : base($"Not a directory: {path}") { Path = path; }
}

/// <summary>
/// Thrown when a file operation is performed on a path that is a directory.
/// </summary>
public class FileStorageIsADirectoryError : Exception
{
    /// <summary>
    /// Gets the path that is a directory.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Initializes a new instance with the path that is a directory.
    /// </summary>
    /// <param name="path">The path that is a directory.</param>
    public FileStorageIsADirectoryError(string path) : base($"Is a directory: {path}") { Path = path; }
}
