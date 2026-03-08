namespace Qodalis.Cli.Plugin.FileSystem;

public interface IFileStorageProvider
{
    string Name { get; }
    Task<List<FileEntry>> ListAsync(string path, CancellationToken ct = default);
    Task<string> ReadFileAsync(string path, CancellationToken ct = default);
    Task WriteFileAsync(string path, string content, CancellationToken ct = default);
    Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default);
    Task<FileStat> StatAsync(string path, CancellationToken ct = default);
    Task MkdirAsync(string path, bool recursive = false, CancellationToken ct = default);
    Task RemoveAsync(string path, bool recursive = false, CancellationToken ct = default);
    Task CopyAsync(string src, string dest, CancellationToken ct = default);
    Task MoveAsync(string src, string dest, CancellationToken ct = default);
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);
    Task<Stream> GetDownloadStreamAsync(string path, CancellationToken ct = default);
    Task UploadFileAsync(string path, byte[] content, CancellationToken ct = default);
}
