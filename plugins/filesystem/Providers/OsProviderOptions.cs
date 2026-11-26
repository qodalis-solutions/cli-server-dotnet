namespace Qodalis.Cli.Plugin.FileSystem;

/// <summary>
/// Configuration options for the OS filesystem storage provider.
/// </summary>
public class OsProviderOptions
{
    /// <summary>
    /// Gets or sets the list of allowed filesystem paths. Only paths under these roots are accessible.
    /// </summary>
    public List<string> AllowedPaths { get; set; } = [];

    /// <summary>Paths never accessible, even below an allowed root (the server's data directory).</summary>
    public List<string> DeniedPaths { get; set; } = [];
}
