using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Plugin.FileSystem;

/// <summary>
/// CLI module that registers the pluggable file storage subsystem.
/// </summary>
public class FileSystemModule : ICliModule
{
    /// <inheritdoc />
    public string Name => "filesystem";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public string Description => "Provides pluggable file storage with in-memory and OS providers";

    /// <inheritdoc />
    public ICliCommandAuthor Author => DefaultLibraryAuthor.Instance;

    /// <inheritdoc />
    public IEnumerable<ICliCommandProcessor> Processors => [];
}
