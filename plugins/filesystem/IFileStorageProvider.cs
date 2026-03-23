namespace Qodalis.Cli.Plugin.FileSystem;

/// <summary>
/// Defines the contract for a pluggable file storage backend.
/// </summary>
public interface IFileStorageProvider
{
    /// <summary>
    /// Gets the unique name identifying this storage provider.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Lists entries (files and directories) at the specified path.
    /// </summary>
    /// <param name="path">The directory path to list.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of file entries in the directory.</returns>
    Task<List<FileEntry>> ListAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Reads the text content of a file at the specified path.
    /// </summary>
    /// <param name="path">The file path to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The file content as a string.</returns>
    Task<string> ReadFileAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Writes text content to a file at the specified path, creating or overwriting it.
    /// </summary>
    /// <param name="path">The file path to write to.</param>
    /// <param name="content">The text content to write.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WriteFileAsync(string path, string content, CancellationToken ct = default);

    /// <summary>
    /// Writes binary content to a file at the specified path, creating or overwriting it.
    /// </summary>
    /// <param name="path">The file path to write to.</param>
    /// <param name="content">The binary content to write.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default);

    /// <summary>
    /// Returns metadata (name, type, size, timestamps) for a file or directory.
    /// </summary>
    /// <param name="path">The path to stat.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>File or directory metadata.</returns>
    Task<FileStat> StatAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Creates a directory at the specified path.
    /// </summary>
    /// <param name="path">The directory path to create.</param>
    /// <param name="recursive">If true, creates intermediate directories as needed.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MkdirAsync(string path, bool recursive = false, CancellationToken ct = default);

    /// <summary>
    /// Removes a file or directory at the specified path.
    /// </summary>
    /// <param name="path">The path to remove.</param>
    /// <param name="recursive">If true, removes directories and their contents recursively.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveAsync(string path, bool recursive = false, CancellationToken ct = default);

    /// <summary>
    /// Copies a file or directory from the source path to the destination path.
    /// </summary>
    /// <param name="src">The source path.</param>
    /// <param name="dest">The destination path.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CopyAsync(string src, string dest, CancellationToken ct = default);

    /// <summary>
    /// Moves (renames) a file or directory from the source path to the destination path.
    /// </summary>
    /// <param name="src">The source path.</param>
    /// <param name="dest">The destination path.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MoveAsync(string src, string dest, CancellationToken ct = default);

    /// <summary>
    /// Checks whether a file or directory exists at the specified path.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the path exists; otherwise false.</returns>
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Opens a readable stream for downloading the file at the specified path.
    /// </summary>
    /// <param name="path">The file path to download.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A readable stream containing the file content.</returns>
    Task<Stream> GetDownloadStreamAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Uploads binary content to a file at the specified path.
    /// </summary>
    /// <param name="path">The destination file path.</param>
    /// <param name="content">The binary content to upload.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UploadFileAsync(string path, byte[] content, CancellationToken ct = default);
}
