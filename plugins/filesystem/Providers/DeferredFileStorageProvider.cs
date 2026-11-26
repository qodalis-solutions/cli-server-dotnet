namespace Qodalis.Cli.Plugin.FileSystem;

/// <summary>
/// A storage created on first use from settings that load after the server's services are registered (a server plugin's
/// settings): <paramref name="create"/> returns the storage, or null while its settings are missing (every call then
/// throws <see cref="FileStorageNotConfiguredError"/>, and is tried again on the next call). Once created it is kept: a
/// change of its settings takes effect after a restart.
/// </summary>
public sealed class DeferredFileStorageProvider(string name, Func<IFileStorageProvider?> create) : IFileStorageProvider
{
    private readonly object _lock = new();
    private IFileStorageProvider? _inner;

    public string Name => name;

    /// <summary>The storage, created now if its settings are set; null otherwise.</summary>
    public IFileStorageProvider? Inner
    {
        get
        {
            lock (_lock) return _inner ??= create();
        }
    }

    private IFileStorageProvider Storage(string path) => Inner ?? throw new FileStorageNotConfiguredError(path, name);

    public Task<List<FileEntry>> ListAsync(string path, CancellationToken ct = default) => Storage(path).ListAsync(path, ct);
    public Task<string> ReadFileAsync(string path, CancellationToken ct = default) => Storage(path).ReadFileAsync(path, ct);
    public Task WriteFileAsync(string path, string content, CancellationToken ct = default) => Storage(path).WriteFileAsync(path, content, ct);
    public Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default) => Storage(path).WriteFileAsync(path, content, ct);
    public Task<FileStat> StatAsync(string path, CancellationToken ct = default) => Storage(path).StatAsync(path, ct);
    public Task MkdirAsync(string path, bool recursive = false, CancellationToken ct = default) => Storage(path).MkdirAsync(path, recursive, ct);
    public Task RemoveAsync(string path, bool recursive = false, CancellationToken ct = default) => Storage(path).RemoveAsync(path, recursive, ct);
    public Task CopyAsync(string src, string dest, CancellationToken ct = default) => Storage(src).CopyAsync(src, dest, ct);
    public Task MoveAsync(string src, string dest, CancellationToken ct = default) => Storage(src).MoveAsync(src, dest, ct);
    public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Storage(path).ExistsAsync(path, ct);
    public Task<Stream> GetDownloadStreamAsync(string path, CancellationToken ct = default) => Storage(path).GetDownloadStreamAsync(path, ct);
    public Task UploadFileAsync(string path, byte[] content, CancellationToken ct = default) => Storage(path).UploadFileAsync(path, content, ct);
}

/// <summary>A storage that lists, reads and downloads; writes, new folders, copies, moves and deletes throw <see cref="FileStorageReadOnlyError"/>.</summary>
public sealed class ReadOnlyFileStorageProvider(IFileStorageProvider inner) : IFileStorageProvider
{
    public IFileStorageProvider Inner => inner;

    public string Name => inner.Name;

    public Task<List<FileEntry>> ListAsync(string path, CancellationToken ct = default) => inner.ListAsync(path, ct);
    public Task<string> ReadFileAsync(string path, CancellationToken ct = default) => inner.ReadFileAsync(path, ct);
    public Task WriteFileAsync(string path, string content, CancellationToken ct = default) => throw new FileStorageReadOnlyError(path);
    public Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default) => throw new FileStorageReadOnlyError(path);
    public Task<FileStat> StatAsync(string path, CancellationToken ct = default) => inner.StatAsync(path, ct);
    public Task MkdirAsync(string path, bool recursive = false, CancellationToken ct = default) => throw new FileStorageReadOnlyError(path);
    public Task RemoveAsync(string path, bool recursive = false, CancellationToken ct = default) => throw new FileStorageReadOnlyError(path);
    public Task CopyAsync(string src, string dest, CancellationToken ct = default) => throw new FileStorageReadOnlyError(dest);
    public Task MoveAsync(string src, string dest, CancellationToken ct = default) => throw new FileStorageReadOnlyError(src);
    public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => inner.ExistsAsync(path, ct);
    public Task<Stream> GetDownloadStreamAsync(string path, CancellationToken ct = default) => inner.GetDownloadStreamAsync(path, ct);
    public Task UploadFileAsync(string path, byte[] content, CancellationToken ct = default) => throw new FileStorageReadOnlyError(path);
}
