using Microsoft.AspNetCore.Http;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.FileSystem;

/// <summary>
/// Wraps the server's <see cref="IFileStorageProvider"/> (registered by <c>AddCli</c>) and publishes <c>fs:changed</c>
/// (audience <c>files:read</c>) after every change, so the filesystem API, the MCP <c>fs_*</c> tools and any other
/// caller of the provider publish the same events whatever the provider.
/// </summary>
public sealed class CliEventFileStorageProvider : IFileStorageProvider
{
    /// <summary>The event type.</summary>
    public const string ChangedEvent = "fs:changed";

    private static readonly CliEventAudience Audience = CliEventAudience.WithPermission(CliRoutePolicy.FilesRead);

    private readonly ICliEventBus _events;
    private readonly IHttpContextAccessor? _http;

    /// <summary>Initializes a new instance of <see cref="CliEventFileStorageProvider"/>.</summary>
    /// <param name="inner">The provider that stores the files.</param>
    /// <param name="events">The bus the events are published on.</param>
    /// <param name="http">Gives the caller of the current request (the event's <c>actor</c>), when available.</param>
    public CliEventFileStorageProvider(IFileStorageProvider inner, ICliEventBus events, IHttpContextAccessor? http = null)
    {
        Inner = inner;
        _events = events;
        _http = http;
    }

    /// <summary>The provider that stores the files.</summary>
    public IFileStorageProvider Inner { get; }

    /// <inheritdoc />
    public string Name => Inner.Name;

    /// <inheritdoc />
    public Task<List<FileEntry>> ListAsync(string path, CancellationToken ct = default) => Inner.ListAsync(path, ct);

    /// <inheritdoc />
    public Task<string> ReadFileAsync(string path, CancellationToken ct = default) => Inner.ReadFileAsync(path, ct);

    /// <inheritdoc />
    public async Task WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        await Inner.WriteFileAsync(path, content, ct);
        await PublishAsync("write", path);
    }

    /// <inheritdoc />
    public async Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        await Inner.WriteFileAsync(path, content, ct);
        await PublishAsync("write", path);
    }

    /// <inheritdoc />
    public Task<FileStat> StatAsync(string path, CancellationToken ct = default) => Inner.StatAsync(path, ct);

    /// <inheritdoc />
    public async Task MkdirAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        await Inner.MkdirAsync(path, recursive, ct);
        await PublishAsync("mkdir", path);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        await Inner.RemoveAsync(path, recursive, ct);
        await PublishAsync("delete", path);
    }

    /// <inheritdoc />
    public async Task CopyAsync(string src, string dest, CancellationToken ct = default)
    {
        await Inner.CopyAsync(src, dest, ct);
        await PublishAsync("copy", src, dest);
    }

    /// <inheritdoc />
    public async Task MoveAsync(string src, string dest, CancellationToken ct = default)
    {
        await Inner.MoveAsync(src, dest, ct);
        await PublishAsync("move", src, dest);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Inner.ExistsAsync(path, ct);

    /// <inheritdoc />
    public Task<Stream> GetDownloadStreamAsync(string path, CancellationToken ct = default) =>
        Inner.GetDownloadStreamAsync(path, ct);

    /// <inheritdoc />
    public async Task UploadFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        await Inner.UploadFileAsync(path, content, ct);
        await PublishAsync("upload", path);
    }

    private Task PublishAsync(string action, string path, string? newPath = null)
    {
        var data = new Dictionary<string, object?> { ["action"] = action, ["path"] = path };
        if (newPath != null) data["newPath"] = newPath;
        if (_http?.HttpContext?.GetCliPrincipal() is { } principal) data["actor"] = principal.UserName;
        return _events.PublishAsync(ChangedEvent, data, Audience);
    }
}
